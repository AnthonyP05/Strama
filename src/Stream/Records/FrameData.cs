namespace Strama.Records;

/// <summary>
/// A single captured frame: raw BGRA pixel data and its dimensions.
/// BGRA = 4 bytes per pixel, row-major, no padding.
/// </summary>
public readonly record struct FrameData(byte[] Pixels, int Width, int Height);
