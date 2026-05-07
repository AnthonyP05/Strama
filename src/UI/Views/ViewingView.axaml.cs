using Avalonia.Controls;
using Avalonia.Input;
using Strama.UI.Services;
using Strama.UI.ViewModels;

namespace Strama.UI.Views;

public partial class ViewingView : UserControl
{
    private FrameRenderer? _subscribedRenderer;

    public ViewingView()
    {
        InitializeComponent();
        Focusable = true;
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += (_, _) => Focus();
        DetachedFromVisualTree += (_, _) => Unsubscribe();
        KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is ViewingViewModel vm)
        {
            vm.DisconnectCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Unsubscribe();
        if (DataContext is ViewingViewModel vm)
        {
            _subscribedRenderer = vm.Renderer;
            _subscribedRenderer.FrameReady += OnFrameReady;
        }
    }

    private void Unsubscribe()
    {
        if (_subscribedRenderer is not null)
        {
            _subscribedRenderer.FrameReady -= OnFrameReady;
            _subscribedRenderer = null;
        }
    }

    // Avalonia's Image control doesn't redraw automatically when the underlying
    // WriteableBitmap pixel buffer changes — only when the Source reference
    // changes. Nudge the visual tree so the new pixels actually paint.
    private void OnFrameReady() => StreamImage.InvalidateVisual();
}
