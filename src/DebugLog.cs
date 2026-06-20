namespace Strama;

/// <summary>
/// Lightweight gate for verbose diagnostic output (per-packet NAL scans,
/// per-second encode/decode stats, etc.). Off by default in the GUI so release
/// builds stay quiet (#7).
///
/// Enable it by either:
///   • setting the <c>STRAMA_DEBUG</c> environment variable to 1/true, or
///   • setting <see cref="Enabled"/> directly — <c>--console</c> mode does this
///     so the Phase-1 transport regression harness keeps its full trace.
///
/// One-time status lines (encoder selection, "Mode: GPU", fatal errors) should
/// keep using <see cref="System.Console.WriteLine"/> directly; only the spammy
/// per-frame / per-packet output goes through here.
/// </summary>
public static class DebugLog
{
    public static bool Enabled { get; set; } =
        Environment.GetEnvironmentVariable("STRAMA_DEBUG") is "1" or "true" or "TRUE";

    public static void Line(string message)
    {
        if (Enabled) System.Console.WriteLine(message);
    }
}
