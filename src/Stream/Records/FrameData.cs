using System.Buffers;

namespace Strama.Records;

public readonly struct FrameData : IDisposable
{
    public byte[] Pixels { get; }
    public int    Width  { get; }
    public int    Height { get; }

    // True when Pixels was rented from ArrayPool<byte>.Shared.
    private readonly bool _pooled;

    public FrameData(byte[] pixels, int width, int height, bool pooled = false)
    {
        Pixels  = pixels;
        Width   = width;
        Height  = height;
        _pooled = pooled;
    }

    public void Dispose()
    {
        if (_pooled)
            ArrayPool<byte>.Shared.Return(Pixels);
    }
}
