using System.Runtime.InteropServices;
using GameVault.Platform.Input;
using GameVault.Platform.Input.Controllers;
using GameVault.Platform.Video;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Graphics.DirectX;

namespace GameVault.App.Controls;

/// <summary>
/// Shows the game: a Win2D swap chain fed from the <see cref="FrameSink"/> by a dedicated render thread,
/// so frames never pass through the XAML UI thread (SPEC §5.1, §9.1). Draws with nearest-neighbor integer
/// scaling. Key events are forwarded to the input service without any logic (docs/design/input.md §10).
/// </summary>
public sealed partial class GameView : Control
{
    public static readonly DependencyProperty CropOverscanProperty = DependencyProperty.Register(
        nameof(CropOverscan), typeof(bool), typeof(GameView), new PropertyMetadata(true));

    private const int FrameWidth = 256;
    private const int FrameHeight = 240;
    private const int OverscanLines = 8;

    private CanvasSwapChainPanel? _panel;
    private FrameSink? _frames;
    private InputService? _input;
    private Renderer? _renderer;

    public GameView()
    {
        DefaultStyleKey = typeof(GameView);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>Hide the top and bottom 8 lines, like most TVs (SPEC §5.2).</summary>
    public bool CropOverscan
    {
        get => (bool)GetValue(CropOverscanProperty);
        set => SetValue(CropOverscanProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _panel = GetTemplateChild("PART_SwapChainPanel") as CanvasSwapChainPanel;
    }

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (_input?.KeyDown(VirtualKeys.Normalize((int)e.Key, e.KeyStatus.ScanCode)) == true)
        {
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyRoutedEventArgs e)
    {
        if (_input?.KeyUp(VirtualKeys.Normalize((int)e.Key, e.KeyStatus.ScanCode)) == true)
        {
            e.Handled = true;
        }

        base.OnKeyUp(e);
    }

    protected override void OnGotFocus(RoutedEventArgs e)
    {
        // Assigned pads drive the game only while it has focus; otherwise they navigate (input.md §7).
        if (App.TryGetService<ControllerManager>() is { } controllers)
        {
            controllers.GameHasFocus = true;
        }

        base.OnGotFocus(e);
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        if (App.TryGetService<ControllerManager>() is { } controllers)
        {
            controllers.GameHasFocus = false;
        }

        _input?.ReleaseAll(); // input.md §4.4: nothing stays held when focus leaves the game
        base.OnLostFocus(e);
    }

    // Devices and threads start here, never in the constructor (SPEC §9.1).
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_panel is null || _renderer is not null)
        {
            return;
        }

        _frames = App.GetService<FrameSink>();
        _input = App.GetService<InputService>();

        var device = CanvasDevice.GetSharedDevice();
        var dpi = (float)(96 * (XamlRoot?.RasterizationScale ?? 1.0));
        var size = new Size(Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
        var swapChain = new CanvasSwapChain(device, (float)size.Width, (float)size.Height, dpi);
        _panel.SwapChain = swapChain;

        _renderer = new Renderer(swapChain, _frames, size, dpi, CropOverscan);
        SizeChanged += OnSizeChanged;
        Focus(FocusState.Programmatic);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        SizeChanged -= OnSizeChanged;
        _input?.ReleaseAll();
        _renderer?.Dispose();
        _renderer = null;
        if (_panel is not null)
        {
            _panel.SwapChain?.Dispose();
            _panel.SwapChain = null; // breaks the Win2D reference cycle when leaving the page
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var dpi = (float)(96 * (XamlRoot?.RasterizationScale ?? 1.0));
        _renderer?.Resize(new Size(Math.Max(1, e.NewSize.Width), Math.Max(1, e.NewSize.Height)), dpi, CropOverscan);
    }

    /// <summary>Owns the render thread. Only this thread draws to and presents the swap chain.</summary>
    private sealed class Renderer : IDisposable
    {
        private readonly CanvasSwapChain _swapChain;
        private readonly FrameSink _frames;
        private readonly Thread _thread;
        private readonly CancellationTokenSource _stop = new();
        private readonly Lock _layoutGate = new();
        private readonly uint[] _pixels = new uint[FrameWidth * FrameHeight];
        private readonly byte[] _bytes = new byte[FrameWidth * FrameHeight * 4];
        private Size _size;
        private float _dpi;
        private bool _crop;
        private bool _resizePending;

        public Renderer(CanvasSwapChain swapChain, FrameSink frames, Size size, float dpi, bool crop)
        {
            _swapChain = swapChain;
            _frames = frames;
            _size = size;
            _dpi = dpi;
            _crop = crop;
            _thread = new Thread(Run) { Name = "GameVault render", IsBackground = true };
            _thread.Start();
        }

        public void Resize(Size size, float dpi, bool crop)
        {
            lock (_layoutGate)
            {
                _size = size;
                _dpi = dpi;
                _crop = crop;
                _resizePending = true;
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _thread.Join(TimeSpan.FromSeconds(1));
            _stop.Dispose();
        }

        private void Run()
        {
            using var bitmap = CanvasBitmap.CreateFromBytes(
                _swapChain.Device, _bytes, FrameWidth, FrameHeight, DirectXPixelFormat.B8G8R8A8UIntNormalized);
            var handles = new[] { _stop.Token.WaitHandle, _frames.FrameReady };

            while (!_stop.IsCancellationRequested)
            {
                WaitHandle.WaitAny(handles, TimeSpan.FromMilliseconds(100));
                if (_stop.IsCancellationRequested)
                {
                    break;
                }

                Size size;
                float dpi;
                bool crop;
                lock (_layoutGate)
                {
                    if (_resizePending)
                    {
                        _swapChain.ResizeBuffers((float)_size.Width, (float)_size.Height, _dpi);
                        _resizePending = false;
                    }

                    size = _size;
                    dpi = _dpi;
                    crop = _crop;
                }

                if (_frames.TryGetFrame(out var frame))
                {
                    var palette = _frames.Palette;
                    for (var i = 0; i < _pixels.Length; i++)
                    {
                        _pixels[i] = palette[frame[i] & 0x1FF];
                    }

                    // ARGB as little-endian uint is BGRA in memory, which is the bitmap's format.
                    MemoryMarshal.AsBytes(_pixels.AsSpan()).CopyTo(_bytes);
                    bitmap.SetPixelBytes(_bytes);
                }

                using (var ds = _swapChain.CreateDrawingSession(Colors.Black))
                {
                    var (destination, source) = Layout(size, dpi, crop);
                    ds.DrawImage(bitmap, destination, source, 1f, CanvasImageInterpolation.NearestNeighbor);
                }

                _swapChain.Present();
            }
        }

        /// <summary>Largest whole-number scale that fits, centered (SPEC §9.1: integer scaling at any DPI).</summary>
        private static (Rect Destination, Rect Source) Layout(Size size, float dpi, bool crop)
        {
            var visibleHeight = crop ? FrameHeight - 2 * OverscanLines : FrameHeight;
            var source = new Rect(0, crop ? OverscanLines : 0, FrameWidth, visibleHeight);

            var pixelsPerDip = dpi / 96.0;
            var widthPx = size.Width * pixelsPerDip;
            var heightPx = size.Height * pixelsPerDip;
            var scale = Math.Max(1, (int)Math.Min(widthPx / FrameWidth, heightPx / visibleHeight));

            var destWidth = FrameWidth * scale / pixelsPerDip;
            var destHeight = visibleHeight * scale / pixelsPerDip;
            var destination = new Rect(
                Math.Floor((size.Width - destWidth) / 2 * pixelsPerDip) / pixelsPerDip,
                Math.Floor((size.Height - destHeight) / 2 * pixelsPerDip) / pixelsPerDip,
                destWidth,
                destHeight);
            return (destination, source);
        }
    }
}
