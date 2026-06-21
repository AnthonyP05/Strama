using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Strama.Encode;

/// <summary>
/// Converts an HDR / 10-bit desktop-duplication frame to 8-bit SDR BGRA on the GPU
/// with a small render-to-texture pixel shader. Only constructed when the captured
/// desktop format isn't already <see cref="Format.B8G8R8A8_UNorm"/> — an SDR desktop
/// keeps the existing zero-copy CopyResource path and never touches this class.
///
/// Why a shader and not the D3D11 VideoProcessor: on an HDR display Desktop
/// Duplication hands back R16G16B16A16_FLOAT (scRGB), and the AMD VideoProcessor
/// rejects FP16 RGBA as an input format (CreateVideoProcessorInputView → E_INVALIDARG).
/// A shader-resource view over FP16 is universally supported, so we sample the scRGB
/// texel directly and write SDR sRGB into a BGRA8 render target the encoder can read.
///
/// Conversion: scRGB is linear, Rec.709 primaries, 1.0 = SDR white. SDR desktop
/// content lives in [0,1]; the shader clips HDR highlights (>1) and applies the sRGB
/// transfer function. Same-resolution 1:1 texel fetch, so no sampler/scaling.
/// </summary>
internal sealed class HdrToSdrConverter : IDisposable
{
    // Precompiled with fxc (vs_4_0 / ps_4_0). Source + recompile command in
    // src/Stream/Encode/Shaders/*.hlsl.
    private const string VsBytecodeB64 =
        "RFhCQz+aSUzVceIyN1VOzTCZ3n4BAAAAZAIAAAUAAAA0AAAAgAAAALQAAADoAAAA6AEAAFJERUZEAAAAAAAAAAAAAAAAAAAAHAAAAAAE/v8AgQAAHAAAAE1pY3Jvc29mdCAoUikgSExTTCBTaGFkZXIgQ29tcGlsZXIgMTAuMQBJU0dOLAAAAAEAAAAIAAAAIAAAAAAAAAAGAAAAAQAAAAAAAAABAQAAU1ZfVmVydGV4SUQAT1NHTiwAAAABAAAACAAAACAAAAAAAAAAAQAAAAMAAAAAAAAADwAAAFNWX1Bvc2l0aW9uAFNIRFL4AAAAQAABAD4AAABgAAAEEhAQAAAAAAAGAAAAZwAABPIgEAAAAAAAAQAAAGgAAAIBAAAAKQAABxIAEAAAAAAAChAQAAAAAAABQAAAAQAAAAEAAAcSABAAAAAAAAoAEAAAAAAAAUAAAAIAAAABAAAHQgAQAAAAAAAKEBAAAAAAAAFAAAACAAAAVgAABTIAEAAAAAAAhgAQAAAAAAAyAAAPMiAQAAAAAABGABAAAAAAAAJAAAAAAABAAAAAwAAAAAAAAAAAAkAAAAAAgL8AAIA/AAAAAAAAAAA2AAAIwiAQAAAAAAACQAAAAAAAAAAAAAAAAAAAAAACAPz4AAAFTVEFUdAAAAAcAAAABAAAAAAAAAAIAAAABAAAAAQAAAAIAAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAQAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private const string PsBytecodeB64 =
        "RFhCQ6qFIy2rXoCVSJTkNkSx3lEBAAAASAMAAAUAAAA0AAAApAAAANgAAAAMAQAAzAIAAFJERUZoAAAAAAAAAAAAAAABAAAAHAAAAAAE//8AgQAAQAAAADwAAAACAAAABQAAAAQAAAD/////AAAAAAEAAAANAAAAU3JjAE1pY3Jvc29mdCAoUikgSExTTCBTaGFkZXIgQ29tcGlsZXIgMTAuMQBJU0dOLAAAAAEAAAAIAAAAIAAAAAAAAAABAAAAAwAAAAAAAAAPAwAAU1ZfUG9zaXRpb24AT1NHTiwAAAABAAAACAAAACAAAAAAAAAAAAAAAAMAAAAAAAAADwAAAFNWX1RhcmdldACrq1NIRFK4AQAAQAAAAG4AAABYGAAEAHAQAAAAAABVVQAAZCAABDIQEAAAAAAAAQAAAGUAAAPyIBAAAAAAAGgAAAIDAAAAGwAABTIAEAAAAAAARhAQAAAAAAA2AAAIwgAQAAAAAAACQAAAAAAAAAAAAAAAAAAAAAAAAC0AAAfyABAAAAAAAEYOEAAAAAAARn4QAAAAAAA2IAAFcgAQAAAAAABGAhAAAAAAAC8AAAVyABAAAQAAAEYCEAAAAAAAOAAACnIAEAABAAAARgIQAAEAAAACQAAAVVXVPlVV1T5VVdU+AAAAABkAAAVyABAAAQAAAEYCEAABAAAAMgAAD3IAEAABAAAARgIQAAEAAAACQAAAPQqHPz0Khz89Coc/AAAAAAJAAACuR2G9rkdhva5HYb0AAAAAHQAACnIAEAACAAAAAkAAABwuTTscLk07HC5NOwAAAABGAhAAAAAAADgAAApyABAAAAAAAEYCEAAAAAAAAkAAAFK4TkFSuE5BUrhOQQAAAAA3AAAJciAQAAAAAABGAhAAAgAAAEYCEAAAAAAARgIQAAEAAAA2AAAFgiAQAAAAAAABQAAAAACAPz4AAAFTVEFUdAAAAA0AAAADAAAAAAAAAAIAAAAGAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAAADAAAAAQAAAAEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private readonly ID3D11Device              _device;
    private readonly ID3D11VertexShader        _vs;
    private readonly ID3D11PixelShader         _ps;
    private readonly ID3D11Texture2D           _stagingHdr;   // SRV source (copy of the acquired frame)
    private readonly ID3D11ShaderResourceView  _srv;
    private readonly ID3D11RasterizerState     _rasterizer;   // CullNone so winding can't blank the output
    private readonly Viewport                  _viewport;

    // RTVs are per destination texture; the encoder reuses a small pool, so cache one
    // render-target view per texture rather than recreating it every frame.
    private readonly Dictionary<nint, ID3D11RenderTargetView> _rtvs = new();

    public HdrToSdrConverter(
        ID3D11Device device, ID3D11DeviceContext context,
        int width, int height, Format captureFormat)
    {
        _device   = device;
        _viewport = new Viewport(0, 0, width, height, 0f, 1f);

        _vs = device.CreateVertexShader(System.Convert.FromBase64String(VsBytecodeB64));
        _ps = device.CreatePixelShader(System.Convert.FromBase64String(PsBytecodeB64));

        // The shader samples from an SRV, which needs a texture we control with the
        // ShaderResource bind flag. The acquired duplication texture can't reliably
        // back one, so CopyResource into this staging texture (same HDR format) first.
        _stagingHdr = device.CreateTexture2D(new Texture2DDescription
        {
            Width             = (uint)width,
            Height            = (uint)height,
            MipLevels         = 1,
            ArraySize         = 1,
            Format            = captureFormat,
            SampleDescription = new SampleDescription(1, 0),
            Usage             = ResourceUsage.Default,
            BindFlags         = BindFlags.ShaderResource,
            CPUAccessFlags    = CpuAccessFlags.None,
        });
        _srv = device.CreateShaderResourceView(_stagingHdr);

        _rasterizer = device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.None,
        });
    }

    /// <summary>
    /// Tone-maps <paramref name="acquired"/> (HDR) into <paramref name="destBgra"/>
    /// (8-bit SDR BGRA) on the GPU. Both textures live on the same device/context.
    /// </summary>
    public void Convert(ID3D11DeviceContext context, ID3D11Texture2D acquired, ID3D11Texture2D destBgra)
    {
        context.CopyResource(_stagingHdr, acquired);

        var rtv = GetRenderTargetView(destBgra);

        context.IASetInputLayout(null);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(_vs);
        context.PSSetShader(_ps);
        context.PSSetShaderResource(0, _srv);
        context.RSSetState(_rasterizer);
        context.RSSetViewport(_viewport);
        context.OMSetRenderTargets(rtv);
        context.Draw(3, 0);

        // Unbind so the next frame's CopyResource into the staging texture and the
        // encoder's read of destBgra don't trip read/write hazard warnings.
        context.OMSetRenderTargets((ID3D11RenderTargetView?)null);
        context.PSSetShaderResource(0, null);
    }

    private ID3D11RenderTargetView GetRenderTargetView(ID3D11Texture2D destBgra)
    {
        if (_rtvs.TryGetValue(destBgra.NativePointer, out var existing))
            return existing;

        var rtv = _device.CreateRenderTargetView(destBgra);
        _rtvs[destBgra.NativePointer] = rtv;
        return rtv;
    }

    public void Dispose()
    {
        foreach (var rtv in _rtvs.Values) rtv.Dispose();
        _rtvs.Clear();
        _rasterizer?.Dispose();
        _srv?.Dispose();
        _stagingHdr?.Dispose();
        _ps?.Dispose();
        _vs?.Dispose();
    }
}
