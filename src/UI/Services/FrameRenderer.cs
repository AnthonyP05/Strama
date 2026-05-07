using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Strama.Records;

namespace Strama.UI.Services;

/// <summary>
/// Reads decoded BGRA frames off a channel and copies them onto a single
/// reused <see cref="WriteableBitmap"/>. The bitmap is allocated on the first
/// frame (when the actual stream dimensions are known) and reallocated only on
/// resolution change — per-frame allocation would push ~14 MB/frame of GC at
/// 1440p.
///
/// Subscribers wire <see cref="FrameReady"/> to <c>image.InvalidateVisual()</c>
/// on the actual Image control: Avalonia doesn't redraw bound Images just from
/// the underlying pixels changing, so we have to nudge the visual tree by hand.
/// <see cref="Bitmap"/> changes (initial allocation or resolution change) raise
/// <see cref="INotifyPropertyChanged.PropertyChanged"/> in the usual way.
/// </summary>
public sealed class FrameRenderer : INotifyPropertyChanged, IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private Task?            _runTask;
    private WriteableBitmap? _bitmap;

    public WriteableBitmap? Bitmap
    {
        get => _bitmap;
        private set
        {
            if (_bitmap == value) return;
            _bitmap = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Bitmap)));
        }
    }

    public int FrameCount    { get; private set; }
    public int LastFps       { get; private set; }
    public int CurrentWidth  { get; private set; }
    public int CurrentHeight { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action?                      FrameReady;

    /// <summary>Begins consuming frames from the reader on a background task.</summary>
    public void Start(ChannelReader<FrameData> reader)
    {
        if (_runTask is not null) return;
        _runTask = Task.Run(() => RunAsync(reader, _cts.Token));
    }

    private async Task RunAsync(ChannelReader<FrameData> reader, CancellationToken ct)
    {
        var fpsClock = System.Diagnostics.Stopwatch.StartNew();
        int framesThisSecond = 0;

        try
        {
            await foreach (var f in reader.ReadAllAsync(ct))
            {
                EnsureBitmap(f.Width, f.Height);
                CopyPixels(f);

                framesThisSecond++;
                FrameCount++;

                if (fpsClock.ElapsedMilliseconds >= 1000)
                {
                    LastFps          = framesThisSecond;
                    framesThisSecond = 0;
                    fpsClock.Restart();
                    RaiseProp(nameof(LastFps));
                }

                Dispatcher.UIThread.Post(() => FrameReady?.Invoke(), DispatcherPriority.Render);

                f.Dispose();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[FrameRenderer] {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void EnsureBitmap(int width, int height)
    {
        if (_bitmap is not null
            && _bitmap.PixelSize.Width  == width
            && _bitmap.PixelSize.Height == height) return;

        var fresh = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        CurrentWidth  = width;
        CurrentHeight = height;

        // PropertyChanged fires off-UI-thread; Avalonia bindings marshal it
        // back to the UI thread before applying.
        Bitmap = fresh;
        RaiseProp(nameof(CurrentWidth));
        RaiseProp(nameof(CurrentHeight));
    }

    private void CopyPixels(FrameData f)
    {
        if (_bitmap is null) return;
        using var fb = _bitmap.Lock();
        Marshal.Copy(f.Pixels, 0, fb.Address, f.Width * f.Height * 4);
    }

    private void RaiseProp(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose() => _cts.Cancel();
}
