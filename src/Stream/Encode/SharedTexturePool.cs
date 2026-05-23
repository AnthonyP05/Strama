using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Strama.Encode;

/// <summary>
/// A pool of GPU textures that can live on either a single D3D11 device (cheap
/// same-adapter case) or be mirrored across two devices via shared NT handles
/// with keyed-mutex synchronization (cross-adapter case, e.g. capturing from
/// the iGPU and encoding on an NVENC-capable dGPU).
///
/// In the cross-adapter case the underlying VRAM is bridged by the GPU driver:
/// when the display-side device writes to the texture and signals the keyed
/// mutex, the encoder-side device sees the same data on a later acquire. The
/// driver handles the PCIe transfer.
///
/// Call convention (round-robin index `idx`):
///   AcquireForCapture(idx)  — display device waits until encoder is done
///   ...display device writes...
///   ReleaseAfterCapture(idx)
///   AcquireForEncode(idx)   — encoder device waits until capture is done
///   ...encoder device reads...
///   ReleaseAfterEncode(idx)
/// In the same-adapter case these are no-ops; D3D11's serial command queue on
/// the single device gives the required ordering for free.
/// </summary>
internal sealed class SharedTexturePool : IDisposable
{
    // Mutex keys. The convention is:
    //   key=0 → display side owns it (encoder has released)
    //   key=1 → encoder side owns it (display has released)
    // Acquire waits for the other side to release with the matching key.
    private const ulong KeyDisplayOwns = 0;
    private const ulong KeyEncoderOwns = 1;
    private const int   MutexTimeoutMs = 1000;

    private readonly bool                _crossAdapter;
    private readonly ID3D11Texture2D[]   _displaySide;
    private readonly ID3D11Texture2D[]   _encoderSide;
    private readonly IDXGIKeyedMutex[]?  _displayMutexes;
    private readonly IDXGIKeyedMutex[]?  _encoderMutexes;
    private readonly IntPtr[]?           _sharedHandles;

    public ID3D11Texture2D[] DisplaySide => _displaySide;
    public ID3D11Texture2D[] EncoderSide => _encoderSide;
    public int               Count       => _displaySide.Length;

    private SharedTexturePool(
        bool crossAdapter,
        ID3D11Texture2D[] displaySide,
        ID3D11Texture2D[] encoderSide,
        IDXGIKeyedMutex[]? displayMutexes,
        IDXGIKeyedMutex[]? encoderMutexes,
        IntPtr[]? sharedHandles)
    {
        _crossAdapter   = crossAdapter;
        _displaySide    = displaySide;
        _encoderSide    = encoderSide;
        _displayMutexes = displayMutexes;
        _encoderMutexes = encoderMutexes;
        _sharedHandles  = sharedHandles;
    }

    public static SharedTexturePool Create(
        ID3D11Device displayDevice,
        ID3D11Device encoderDevice,
        int width, int height, Format format, int poolSize,
        bool crossAdapter)
    {
        if (!crossAdapter)
        {
            // Single-device case: just a plain pool, no sharing or mutexes.
            var desc = new Texture2DDescription
            {
                Width             = (uint)width,
                Height            = (uint)height,
                MipLevels         = 1,
                ArraySize         = 1,
                Format            = format,
                SampleDescription = new SampleDescription(1, 0),
                Usage             = ResourceUsage.Default,
                BindFlags         = BindFlags.ShaderResource,
                CPUAccessFlags    = CpuAccessFlags.None,
            };
            var tex = new ID3D11Texture2D[poolSize];
            for (int i = 0; i < poolSize; i++)
                tex[i] = displayDevice.CreateTexture2D(desc);
            return new SharedTexturePool(false, tex, tex, null, null, null);
        }

        // Cross-adapter case: each texture is created on the display device with
        // SharedNthandle + SharedKeyedMutex, then opened on the encoder device.
        // D3D11 requires keyed-mutex + NT-handle resources to have RenderTarget
        // or UnorderedAccess bind flags — ShaderResource alone fails with
        // E_INVALIDARG at CreateTexture2D.
        var sharedDesc = new Texture2DDescription
        {
            Width             = (uint)width,
            Height            = (uint)height,
            MipLevels         = 1,
            ArraySize         = 1,
            Format            = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage             = ResourceUsage.Default,
            BindFlags         = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CPUAccessFlags    = CpuAccessFlags.None,
            MiscFlags         = ResourceOptionFlags.SharedNTHandle | ResourceOptionFlags.SharedKeyedMutex,
        };

        var displayTex = new ID3D11Texture2D[poolSize];
        var encoderTex = new ID3D11Texture2D[poolSize];
        var displayMtx = new IDXGIKeyedMutex[poolSize];
        var encoderMtx = new IDXGIKeyedMutex[poolSize];
        var handles    = new IntPtr[poolSize];

        Console.WriteLine($"[Pool] Creating shared pool {width}x{height} format={format} on display device");
        using var encoderDevice1 = encoderDevice.QueryInterface<ID3D11Device1>();
        Console.WriteLine("[Pool] Got ID3D11Device1 on encoder device");

        for (int i = 0; i < poolSize; i++)
        {
            try
            {
                displayTex[i] = displayDevice.CreateTexture2D(sharedDesc);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"CreateTexture2D failed for shared texture {i} (format={format}, size={width}x{height})", ex);
            }

            IDXGIResource1 resource1;
            try
            {
                resource1 = displayTex[i].QueryInterface<IDXGIResource1>();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"QueryInterface<IDXGIResource1> failed for shared texture {i}", ex);
            }

            try
            {
                handles[i] = resource1.CreateSharedHandle(
                    null,
                    Vortice.DXGI.SharedResourceFlags.Read | Vortice.DXGI.SharedResourceFlags.Write,
                    null);
            }
            catch (Exception ex)
            {
                resource1.Dispose();
                throw new InvalidOperationException(
                    $"CreateSharedHandle failed for shared texture {i}", ex);
            }
            resource1.Dispose();

            try
            {
                encoderTex[i] = encoderDevice1.OpenSharedResource1<ID3D11Texture2D>(handles[i]);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"OpenSharedResource1 failed for shared texture {i} (handle=0x{handles[i].ToInt64():X})", ex);
            }

            try
            {
                displayMtx[i] = displayTex[i].QueryInterface<IDXGIKeyedMutex>();
                encoderMtx[i] = encoderTex[i].QueryInterface<IDXGIKeyedMutex>();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"QueryInterface<IDXGIKeyedMutex> failed for shared texture {i}", ex);
            }
        }
        Console.WriteLine($"[Pool] Successfully created {poolSize} shared cross-adapter textures");

        return new SharedTexturePool(true, displayTex, encoderTex, displayMtx, encoderMtx, handles);
    }

    public void AcquireForCapture(int idx)
    {
        if (!_crossAdapter) return;
        _displayMutexes![idx].AcquireSync(KeyDisplayOwns, MutexTimeoutMs);
    }

    public void ReleaseAfterCapture(int idx)
    {
        if (!_crossAdapter) return;
        _displayMutexes![idx].ReleaseSync(KeyEncoderOwns);
    }

    public void AcquireForEncode(int idx)
    {
        if (!_crossAdapter) return;
        _encoderMutexes![idx].AcquireSync(KeyEncoderOwns, MutexTimeoutMs);
    }

    public void ReleaseAfterEncode(int idx)
    {
        if (!_crossAdapter) return;
        _encoderMutexes![idx].ReleaseSync(KeyDisplayOwns);
    }

    /// <summary>
    /// Primes mutex state so the first AcquireForCapture call doesn't block.
    /// Initial mutex state on creation is "key=0 acquired by no one" — this
    /// sets it so the display side can take it on the first iteration.
    /// </summary>
    public void PrimeForFirstCapture()
    {
        if (!_crossAdapter) return;
        // The mutex starts with key=0 owned by the creating device. We need to
        // be able to AcquireSync(0) at the start of capture — but the mutex
        // already considers key=0 "available" right after creation, so the
        // first AcquireForCapture is non-blocking. No prime needed in practice.
    }

    public void Dispose()
    {
        if (_encoderMutexes != null) foreach (var m in _encoderMutexes) m.Dispose();
        if (_displayMutexes != null) foreach (var m in _displayMutexes) m.Dispose();

        // Encoder-side textures only exist as separate objects in cross-adapter mode.
        if (_crossAdapter)
            foreach (var t in _encoderSide) t.Dispose();

        foreach (var t in _displaySide) t.Dispose();

        if (_sharedHandles != null)
            foreach (var h in _sharedHandles)
                if (h != IntPtr.Zero) CloseHandle(h);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
