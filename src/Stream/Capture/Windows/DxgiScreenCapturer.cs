using System.Buffers;
using System.Runtime.InteropServices;
using Strama.Records;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Strama.Capture.Windows;

public class DxgiScreenCapturer : IScreenCapturer
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGIOutputDuplication _duplication = null!;

    // Created lazily on the first frame. Reused every frame after that.
    // If the desktop resolution changes mid-session, it gets recreated.
    private ID3D11Texture2D? _stagingTexture;

    public DxgiScreenCapturer(int monitorIndex = 0)
    {
        // Create a D3D11 device on the default GPU.
        // This overload throws on failure and returns the device directly.
        // We then grab the immediate context from it — the context is what
        // we use later to issue GPU copy commands.
        _device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.None);
        _context = _device.ImmediateContext;

        // Cast to IDXGIDevice so we can walk up the DXGI object hierarchy.
        // DXGI sits underneath D3D11 and owns the display infrastructure.
        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();

        // The adapter is the physical GPU. From it we can list attached monitors.
        using var adapter = dxgiDevice.GetParent<IDXGIAdapter>();

        // EnumOutputs lists monitors by index. 0 is always the primary display.
        // Unlike most C# APIs this follows the COM pattern: returns a Result
        // (success/error code) and gives the object via an out parameter.
        adapter.EnumOutputs((uint)monitorIndex, out var output).CheckError();
        using (output)
        {
            // DuplicateOutput only exists on IDXGIOutput1, not the base IDXGIOutput.
            using var output1 = output.QueryInterface<IDXGIOutput1>();

            // Creates the Desktop Duplication object — our tap into the DWM compositor.
            // From here we can request a copy of the desktop texture each frame.
            // Vortice wraps the COM out-param as the return value, so it returns
            // the duplication object directly and throws on failure.
            _duplication = output1.DuplicateOutput(_device);
        }
    }

    /// <summary>
    /// Captures the next changed frame from the desktop.
    /// Returns null if no new frame appeared within <paramref name="timeoutMs"/>.
    /// Throws <see cref="SharpGen.Runtime.SharpGenException"/> with ResultCode.AccessLost
    /// if the desktop session changed (lock screen, UAC prompt, resolution change) —
    /// the caller should dispose this instance and create a new one.
    /// </summary>
    public FrameData? CaptureFrame(int timeoutMs = 100)
    {
        // AcquireNextFrame blocks until the desktop compositor (DWM) has a new
        // frame ready, or until the timeout expires.
        // It returns a Result (HRESULT) rather than throwing, so we check it manually.
        var result = _duplication.AcquireNextFrame(
            (uint)timeoutMs,
            out _,             // frameInfo — contains dirty rects and cursor info, unused for now
            out var desktopResource);

        // WaitTimeout just means the screen didn't change within the window — not an error.
        if (result == Vortice.DXGI.ResultCode.WaitTimeout)
            return null;

        // Any other failure (e.g. AccessLost) becomes an exception.
        result.CheckError();

        using (desktopResource)
        {
            // The resource handed back is actually a D3D11 texture on the GPU.
            // We cast to ID3D11Texture2D to access its description and use it as a copy source.
            using var acquiredTexture = desktopResource.QueryInterface<ID3D11Texture2D>();
            var desc = acquiredTexture.Description;

            // Staging texture: same size and format as the desktop texture, but created with
            // Usage.Staging + CpuAccessFlags.Read so that the CPU can read its contents.
            // The GPU-side acquired texture (Usage.Default) cannot be read by the CPU directly.
            // We create it once and reuse it every frame. If the resolution changes, recreate it.
            if (_stagingTexture is null ||
                _stagingTexture.Description.Width != desc.Width ||
                _stagingTexture.Description.Height != desc.Height)
            {
                _stagingTexture?.Dispose();
                _stagingTexture = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = desc.Width,
                    Height = desc.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = desc.Format,                 // BGRA8 on most systems
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read,
                });
            }

            // Ask the GPU to copy the acquired desktop texture into our staging texture.
            // This is a GPU-side operation and completes before CopyResource returns.
            _context.CopyResource(_stagingTexture, acquiredTexture);
        }

        // Release the frame as soon as the GPU copy is done.
        // DXGI can't give us the next frame until we call this.
        _duplication.ReleaseFrame().CheckError();

        // Map locks the staging texture and gives us a CPU pointer to its memory.
        _context.Map(_stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped);
        try
        {
            int width  = (int)_stagingTexture.Description.Width;
            int height = (int)_stagingTexture.Description.Height;

            // Rent a buffer from the pool instead of allocating a new array every frame.
            // ArrayPool.Rent may return a larger buffer than requested — that's fine,
            // because all reads/writes use explicit width*height*4 bounds, not pixels.Length.
            int size   = width * height * 4;
            var pixels = ArrayPool<byte>.Shared.Rent(size);

            // RowPitch is the number of bytes per row as laid out in GPU memory.
            // It is often larger than width * 4 due to alignment padding.
            // We must copy one row at a time and skip the padding between rows.
            for (int row = 0; row < height; row++)
            {
                Marshal.Copy(
                    source:      mapped.DataPointer + row * (int)mapped.RowPitch,
                    destination: pixels,
                    startIndex:  row * width * 4,
                    length:      width * 4);
            }

            return new FrameData(pixels, width, height, pooled: true);
        }
        finally
        {
            // Always unmap — leaving a texture mapped blocks the GPU from using it.
            _context.Unmap(_stagingTexture, 0);
        }
    }

    public void Dispose()
    {
        _stagingTexture?.Dispose();
        _duplication.Dispose();
        _context.Dispose();
        _device.Dispose();
        GC.SuppressFinalize(this);
    }
}
