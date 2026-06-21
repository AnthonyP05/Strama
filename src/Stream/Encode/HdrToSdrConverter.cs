using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Strama.Encode;

/// <summary>
/// Converts an HDR / 10-bit desktop-duplication frame to 8-bit SDR BGRA using the
/// D3D11 VideoProcessor, which performs the colour-space conversion and tone-mapping
/// in hardware. This is only constructed when the captured desktop format isn't
/// already <see cref="Format.B8G8R8A8_UNorm"/> — an SDR desktop keeps the existing
/// zero-copy CopyResource path and never touches this class.
///
/// Why it's needed: on an HDR display, DXGI Desktop Duplication hands back
/// R16G16B16A16_FLOAT (scRGB) or R10G10B10A2 (HDR10), not 8-bit BGRA. Feeding those
/// bytes to the H.264 encoder as if they were BGRA produces purple/sheared garbage.
/// The VideoProcessor reads them in their real colour space and writes proper SDR
/// sRGB BGRA that the encoder pipeline expects.
/// </summary>
internal sealed class HdrToSdrConverter : IDisposable
{
    private readonly ID3D11VideoDevice               _videoDevice;
    private readonly ID3D11VideoContext1             _videoContext;
    private readonly ID3D11VideoProcessor            _processor;
    private readonly ID3D11VideoProcessorEnumerator  _enumerator;
    private readonly ID3D11Texture2D                 _stagingHdr;   // VP input (copy of the acquired frame)
    private readonly ID3D11VideoProcessorInputView   _inputView;

    // VP output views are per-destination-texture; the encoder reuses a small pool
    // of BGRA textures, so cache one output view per texture rather than recreating.
    private readonly Dictionary<nint, ID3D11VideoProcessorOutputView> _outputViews = new();

    public HdrToSdrConverter(
        ID3D11Device device, ID3D11DeviceContext context,
        int width, int height, Format captureFormat)
    {
        _videoDevice  = device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = context.QueryInterface<ID3D11VideoContext1>();

        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate   = new Rational(60, 1),
            InputWidth       = (uint)width,
            InputHeight      = (uint)height,
            OutputFrameRate  = new Rational(60, 1),
            OutputWidth      = (uint)width,
            OutputHeight     = (uint)height,
            Usage            = VideoUsage.PlaybackNormal,
        };
        _enumerator = _videoDevice.CreateVideoProcessorEnumerator(content);
        _processor  = _videoDevice.CreateVideoProcessor(_enumerator, 0);

        // Tell the VP what it's reading (HDR) and producing (SDR sRGB) so it
        // tone-maps rather than reinterpreting the bytes.
        var inputColorSpace = captureFormat == Format.R10G10B10A2_UNorm
            ? ColorSpaceType.RgbFullG2084NoneP2020   // HDR10: PQ transfer, Rec.2020 primaries
            : ColorSpaceType.RgbFullG10NoneP709;      // scRGB: linear FP16, Rec.709 primaries
        _videoContext.VideoProcessorSetStreamColorSpace1(_processor, 0, inputColorSpace);
        _videoContext.VideoProcessorSetOutputColorSpace1(_processor, ColorSpaceType.RgbFullG22NoneP709);

        // The VP reads from an input view, which must be backed by a texture we
        // control. The acquired duplication texture can't reliably back one, so we
        // CopyResource into this staging texture (same HDR format) each frame.
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

        _inputView = _videoDevice.CreateVideoProcessorInputView(
            _stagingHdr, _enumerator,
            new VideoProcessorInputViewDescription
            {
                FourCC        = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D     = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
            });
    }

    /// <summary>
    /// Tone-maps <paramref name="acquired"/> (HDR) into <paramref name="destBgra"/>
    /// (8-bit SDR BGRA) on the GPU. Both textures live on the same device/context.
    /// </summary>
    public void Convert(ID3D11DeviceContext context, ID3D11Texture2D acquired, ID3D11Texture2D destBgra)
    {
        context.CopyResource(_stagingHdr, acquired);

        var outputView = GetOutputView(destBgra);
        var stream = new VideoProcessorStream
        {
            Enable       = true,
            InputSurface = _inputView,
        };
        _videoContext.VideoProcessorBlt(_processor, outputView, 0, new[] { stream });
    }

    private ID3D11VideoProcessorOutputView GetOutputView(ID3D11Texture2D destBgra)
    {
        if (_outputViews.TryGetValue(destBgra.NativePointer, out var existing))
            return existing;

        var view = _videoDevice.CreateVideoProcessorOutputView(
            destBgra, _enumerator,
            new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                Texture2D     = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
            });
        _outputViews[destBgra.NativePointer] = view;
        return view;
    }

    public void Dispose()
    {
        foreach (var view in _outputViews.Values) view.Dispose();
        _outputViews.Clear();
        _inputView?.Dispose();
        _stagingHdr?.Dispose();
        _processor?.Dispose();
        _enumerator?.Dispose();
        _videoContext?.Dispose();
        _videoDevice?.Dispose();
    }
}
