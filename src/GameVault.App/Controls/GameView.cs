using System.Runtime.InteropServices;
using GameVault.Platform.Input;
using GameVault.Platform.Input.Controllers;
using GameVault.Platform.Video;
using GameVault.ViewModels.Services;
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
/// Shows the game. A dedicated render thread feeds frames from the <see cref="FrameSink"/> to a Direct3D 11
/// swap chain with the chosen display preset (SPEC §5.2), so frames never pass through the XAML UI thread
/// (SPEC §5.1, §9.1). If Direct3D 11 can't start, a Win2D swap chain with integer scaling takes over.
/// Key events are forwarded to the input service without any logic (docs/design/input.md §10).
/// </summary>
public sealed partial class GameView : Control
{
    public static readonly DependencyProperty CropOverscanProperty = DependencyProperty.Register(
        nameof(CropOverscan), typeof(bool), typeof(GameView), new PropertyMetadata(true));

    /// <summary>When true the mouse aims the Zapper and the left button pulls the trigger (SPEC §4.7, §7.5).</summary>
    public static readonly DependencyProperty ZapperEnabledProperty = DependencyProperty.Register(
        nameof(ZapperEnabled), typeof(bool), typeof(GameView), new PropertyMetadata(false, OnZapperEnabledChanged));

    private const int FrameWidth = 256;
    private const int FrameHeight = 240;
    private const int OverscanLines = 8;

    private CanvasSwapChainPanel? _panel;
    private SwapChainPanel? _d3dPanel;
    private FrameSink? _frames;
    private InputService? _input;
    private Renderer? _renderer;
    private D3DRenderLoop? _d3d;
    private bool _loaded;
    private AppSettings _settings = new();

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

    public bool ZapperEnabled
    {
        get => (bool)GetValue(ZapperEnabledProperty);
        set => SetValue(ZapperEnabledProperty, value);
    }

    private static void OnZapperEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (GameView)d;
        view.ProtectedCursor = (bool)e.NewValue
            ? Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Cross)
            : null;
        if (!(bool)e.NewValue)
        {
            view._input?.SetZapper(null, trigger: false);
        }
    }

    protected override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        UpdateZapper(e);
        base.OnPointerMoved(e);
    }

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        UpdateZapper(e);
        base.OnPointerPressed(e);
    }

    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        UpdateZapper(e);
        base.OnPointerReleased(e);
    }

    protected override void OnPointerExited(PointerRoutedEventArgs e)
    {
        if (ZapperEnabled)
        {
            _input?.SetZapper(null, trigger: false); // pointing away from the screen
        }

        base.OnPointerExited(e);
    }

    /// <summary>Maps the pointer to NES pixels through the same layout the renderer uses.</summary>
    private void UpdateZapper(PointerRoutedEventArgs e)
    {
        if (!ZapperEnabled || _input is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        var scale = XamlRoot?.RasterizationScale ?? 1.0;
        var rect = DisplayLayout.Compute(
            (int)(ActualWidth * scale),
            (int)(ActualHeight * scale),
            _settings.DisplayPreset,
            CropOverscan,
            _settings.TvPixelShape);
        var px = point.Position.X * scale - rect.X;
        var py = point.Position.Y * scale - rect.Y;
        (int X, int Y)? aim = px >= 0 && py >= 0 && px < rect.Width && py < rect.Height
            ? ((int)(px * FrameWidth / rect.Width), rect.SourceTop + (int)(py * rect.SourceHeight / rect.Height))
            : null;
        _input.SetZapper(aim, point.Properties.IsLeftButtonPressed);
        e.Handled = true;
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _panel = GetTemplateChild("PART_SwapChainPanel") as CanvasSwapChainPanel;
        _d3dPanel = GetTemplateChild("PART_D3DPanel") as SwapChainPanel;
    }

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        // Alt+Enter is full screen (a page accelerator), not Start: let it through untouched.
        var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (alt && e.Key == Windows.System.VirtualKey.Enter)
        {
            base.OnKeyDown(e);
            return;
        }

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
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_panel is null || _loaded)
        {
            return;
        }

        _loaded = true;
        _frames = App.GetService<FrameSink>();
        _input = App.GetService<InputService>();
        Focus(FocusState.Programmatic);

        _settings = await App.GetService<ISettingsStore>().LoadAsync();
        if (!_loaded)
        {
            return; // left the page while settings were loading
        }

        CropOverscan = _settings.CropOverscan;
        SizeChanged += OnSizeChanged;
        if (_d3dPanel is not null && TryStartDirect3D(_d3dPanel))
        {
            return;
        }

        StartWin2D(_panel);
    }

    private bool TryStartDirect3D(SwapChainPanel panel)
    {
        try
        {
            var (width, height) = PhysicalSize(panel, new Size(ActualWidth, ActualHeight));
            var renderer = new D3D11FrameRenderer(width, height);
            SwapChainPanelInterop.SetSwapChain(panel, renderer.SwapChainPointer);
            renderer.SetCompositionScale(panel.CompositionScaleX, panel.CompositionScaleY);
            _d3d = new D3DRenderLoop(renderer, _frames!, _settings.DisplayPreset, CropOverscan, _settings.TvPixelShape);
            panel.CompositionScaleChanged += OnCompositionScaleChanged;
            return true;
        }
        catch (Exception ex)
        {
            // Old drivers, remote sessions without D3D11, a missing d3dcompiler: fall back to Win2D.
            Platform.Storage.CrashLog.Write(new Platform.Storage.AppPaths(), ex, "Direct3D 11 display unavailable; using Win2D");
            _d3dPanel!.Visibility = Visibility.Collapsed;
            return false;
        }
    }

    private void StartWin2D(CanvasSwapChainPanel panel)
    {
        _panel = panel;
        _panel.Visibility = Visibility.Visible;
        var device = CanvasDevice.GetSharedDevice();
        var dpi = (float)(96 * (XamlRoot?.RasterizationScale ?? 1.0));
        var size = new Size(Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
        var swapChain = new CanvasSwapChain(device, (float)size.Width, (float)size.Height, dpi);
        _panel.SwapChain = swapChain;

        _renderer = new Renderer(swapChain, _frames!, size, dpi, CropOverscan, _settings.DisplayPreset == DisplayPreset.SharpPixels);
    }

    private static (int Width, int Height) PhysicalSize(SwapChainPanel panel, Size size) =>
        ((int)Math.Max(1, Math.Round(size.Width * panel.CompositionScaleX)),
         (int)Math.Max(1, Math.Round(size.Height * panel.CompositionScaleY)));

    private void OnCompositionScaleChanged(SwapChainPanel sender, object args)
    {
        var (width, height) = PhysicalSize(sender, new Size(ActualWidth, ActualHeight));
        _d3d?.Resize(width, height, sender.CompositionScaleX, sender.CompositionScaleY);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        SizeChanged -= OnSizeChanged;
        _input?.ReleaseAll();
        if (_d3dPanel is not null)
        {
            _d3dPanel.CompositionScaleChanged -= OnCompositionScaleChanged;
        }

        _d3d?.Dispose(); // stops the render thread before the swap chain goes away
        _d3d = null;
        if (_d3dPanel is not null && _d3dPanel.Visibility == Visibility.Visible)
        {
            try
            {
                SwapChainPanelInterop.SetSwapChain(_d3dPanel, IntPtr.Zero);
            }
            catch (Exception)
            {
                // The panel is going away anyway.
            }
        }

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
        if (_d3d is not null && _d3dPanel is not null)
        {
            var (width, height) = PhysicalSize(_d3dPanel, e.NewSize);
            _d3d.Resize(width, height, _d3dPanel.CompositionScaleX, _d3dPanel.CompositionScaleY);
            return;
        }

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
        private readonly bool _sharp;
        private bool _crop;
        private bool _resizePending;

        public Renderer(CanvasSwapChain swapChain, FrameSink frames, Size size, float dpi, bool crop, bool sharp)
        {
            _sharp = sharp;
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
                    ds.DrawImage(bitmap, destination, source, 1f,
                        _sharp ? CanvasImageInterpolation.NearestNeighbor : CanvasImageInterpolation.Linear);
                }

                _swapChain.Present();
            }
        }

        /// <summary>Placement from <see cref="DisplayLayout"/> in physical pixels, converted to DIPs.</summary>
        private (Rect Destination, Rect Source) Layout(Size size, float dpi, bool crop)
        {
            var pixelsPerDip = dpi / 96.0;
            var rect = DisplayLayout.Compute(
                (int)(size.Width * pixelsPerDip),
                (int)(size.Height * pixelsPerDip),
                _sharp ? DisplayPreset.SharpPixels : DisplayPreset.Smooth,
                crop,
                tvPixelShape: false);
            var source = new Rect(0, rect.SourceTop, FrameWidth, rect.SourceHeight);
            var destination = new Rect(
                rect.X / pixelsPerDip, rect.Y / pixelsPerDip, rect.Width / pixelsPerDip, rect.Height / pixelsPerDip);
            return (destination, source);
        }
    }

    /// <summary>Owns the Direct3D render thread. Only this thread uses the renderer after creation.</summary>
    private sealed class D3DRenderLoop : IDisposable
    {
        private readonly D3D11FrameRenderer _renderer;
        private readonly FrameSink _frames;
        private readonly DisplayPreset _preset;
        private readonly bool _crop;
        private readonly bool _tvPixelShape;
        private readonly Thread _thread;
        private readonly CancellationTokenSource _stop = new();
        private readonly Lock _layoutGate = new();
        private (int Width, int Height, float ScaleX, float ScaleY)? _pendingResize;

        public D3DRenderLoop(D3D11FrameRenderer renderer, FrameSink frames, DisplayPreset preset, bool crop, bool tvPixelShape)
        {
            _renderer = renderer;
            _frames = frames;
            _preset = preset;
            _crop = crop;
            _tvPixelShape = tvPixelShape;
            _thread = new Thread(Run) { Name = "GameVault render (D3D11)", IsBackground = true };
            _thread.Start();
        }

        public void Resize(int width, int height, float scaleX, float scaleY)
        {
            lock (_layoutGate)
            {
                _pendingResize = (width, height, scaleX, scaleY);
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _thread.Join(TimeSpan.FromSeconds(1));
            _stop.Dispose();
            _renderer.Dispose();
        }

        private void Run()
        {
            var handles = new[] { _stop.Token.WaitHandle, _frames.FrameReady };
            while (!_stop.IsCancellationRequested)
            {
                WaitHandle.WaitAny(handles, TimeSpan.FromMilliseconds(100));
                if (_stop.IsCancellationRequested)
                {
                    break;
                }

                lock (_layoutGate)
                {
                    if (_pendingResize is { } r)
                    {
                        _renderer.Resize(r.Width, r.Height);
                        _renderer.SetCompositionScale(r.ScaleX, r.ScaleY);
                        _pendingResize = null;
                    }
                }

                if (_frames.TryGetFrame(out var frame))
                {
                    _renderer.UploadFrame(frame, _frames.Palette);
                }

                _renderer.Render(_preset, _crop, _tvPixelShape);
            }
        }
    }
}
