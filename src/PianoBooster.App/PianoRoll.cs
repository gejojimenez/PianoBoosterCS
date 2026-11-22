// The widgets of GuiPianoRoll.h/.cpp of PianoBooster (C++), GPLv3, in Avalonia: they give the
// models of PianoBooster.Rendering (PianoRollView, SongOverview) the size, the mouse and the
// scroll bar, and draw them with Skia.
//
// The drawing is recorded into an SKPicture in the thread of the interface (where the model
// lives) and the render thread of Avalonia only plays it back.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
using PianoBooster.Rendering;
using SkiaSharp;

namespace PianoBooster.App;

/// <summary>Plays back an SKPicture recorded in the thread of the interface.</summary>
internal sealed class PictureDrawOperation : ICustomDrawOperation
{
    private readonly SKPicture _picture;

    public PictureDrawOperation(Rect bounds, SKPicture picture)
    {
        Bounds = bounds;
        _picture = picture;
    }

    public Rect Bounds { get; }

    public bool HitTest(Point p) => Bounds.Contains(p);

    public bool Equals(ICustomDrawOperation? other) => false;

    public void Render(ImmediateDrawingContext context)
    {
        var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (leaseFeature == null)
            return; // not drawn with Skia
        using ISkiaSharpApiLease lease = leaseFeature.Lease();
        lease.SkCanvas.Save();
        lease.SkCanvas.ClipRect(SKRect.Create((float)Bounds.Width, (float)Bounds.Height));
        lease.SkCanvas.DrawPicture(_picture);
        lease.SkCanvas.Restore();
    }

    public void Dispose() => _picture.Dispose();

    /// <summary>Records a drawing of a size.</summary>
    public static PictureDrawOperation Record(Size size, Action<SKCanvas> draw)
    {
        using var recorder = new SKPictureRecorder();
        SKCanvas canvas = recorder.BeginRecording(SKRect.Create((float)size.Width, (float)size.Height));
        draw(canvas);
        return new PictureDrawOperation(new Rect(size), recorder.EndRecording());
    }

    public static PianoRollPalette PaletteOf(Control control) =>
        control.ActualThemeVariant == ThemeVariant.Dark ? PianoRollPalette.Dark : PianoRollPalette.Light;

    public static Cursor? CursorOf(PianoRollCursor cursor) => cursor switch
    {
        PianoRollCursor.SizeHorizontal => new Cursor(StandardCursorType.SizeWestEast),
        PianoRollCursor.PointingHand => new Cursor(StandardCursorType.Hand),
        _ => null,
    };
}

/// <summary>The viewport of the piano roll (the viewport() of the QAbstractScrollArea).</summary>
internal sealed class PianoRollViewport : Control
{
    private readonly PianoRollView _view;
    private bool _leftPressed;

    public PianoRollViewport(PianoRollView view)
    {
        _view = view;
        ClipToBounds = true;
        Focusable = true; // Qt::StrongFocus
        _view.UpdateRequested += InvalidateVisual;
        _view.CursorChanged += () => Cursor = PictureDrawOperation.CursorOf(_view.Cursor);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _view.Resize((int)e.NewSize.Width, (int)e.NewSize.Height);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ThemeVariantScope.ActualThemeVariantProperty)
        {
            _view.Palette = PictureDrawOperation.PaletteOf(this);
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        _view.Palette = PictureDrawOperation.PaletteOf(this);
        context.Custom(PictureDrawOperation.Record(Bounds.Size, _view.Render));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        PointerPoint point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed || point.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
            return;
        _leftPressed = true;
        e.Pointer.Capture(this);
        _view.MousePress(point.Position.X, point.Position.Y);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point position = e.GetPosition(this);
        _view.MouseMove(position.X, position.Y);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left || !_leftPressed)
            return;
        _leftPressed = false;
        e.Pointer.Capture(null);
        _view.MouseRelease();
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_leftPressed)
        {
            _leftPressed = false;
            _view.MouseRelease();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        // Avalonia gives steps of the wheel; Qt's angleDelta gives 120 for each step
        _view.Wheel(e.Delta.X * 120.0, e.Delta.Y * 120.0, (e.KeyModifiers & KeyModifiers.Control) != 0, e.GetPosition(this).X);
        e.Handled = true;
    }
}

/// <summary>The piano roll of the section editor (CPianoRollView): the view and its horizontal
/// scroll bar (always shown, as Qt::ScrollBarAlwaysOn). The model is <see cref="View"/>.</summary>
public sealed class PianoRollControl : DockPanel
{
    private readonly ScrollBar _scrollBar;
    private int _updatingScrollBar;

    public PianoRollControl()
    {
        View = new PianoRollView();
        MinHeight = 200;
        _scrollBar = new ScrollBar { Orientation = Orientation.Horizontal, AllowAutoHide = false };
        DockPanel.SetDock(_scrollBar, Dock.Bottom);
        Children.Add(_scrollBar);
        Children.Add(new PianoRollViewport(View));

        ScrollBarModel model = View.HorizontalScrollBar;
        model.RangeChanged += UpdateScrollBar;
        model.ValueChanged += UpdateScrollBar;
        _scrollBar.ValueChanged += (_, e) =>
        {
            if (_updatingScrollBar == 0)
                model.Value = (int)Math.Round(e.NewValue);
        };
        UpdateScrollBar();
    }

    public PianoRollView View { get; }

    private void UpdateScrollBar()
    {
        ScrollBarModel model = View.HorizontalScrollBar;
        _updatingScrollBar++;
        try
        {
            _scrollBar.Minimum = model.Minimum;
            _scrollBar.Maximum = model.Maximum;
            _scrollBar.ViewportSize = model.PageStep;
            _scrollBar.LargeChange = model.PageStep;
            _scrollBar.SmallChange = model.SingleStep;
            _scrollBar.Value = model.Value;
        }
        finally
        {
            _updatingScrollBar--;
        }
    }
}

/// <summary>The overview of the song (CSongOverview). The model is <see cref="Overview"/>.</summary>
public sealed class SongOverviewControl : Control
{
    public SongOverviewControl()
    {
        Overview = new SongOverview();
        MinHeight = 40;
        Height = 48; // the sizeHint, with QSizePolicy::Fixed
        Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(this, Strings.Tr("The whole song: click or drag to go to a place"));
        ClipToBounds = true;
        Overview.UpdateRequested += InvalidateVisual;
    }

    public SongOverview Overview { get; }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Overview.Resize((int)e.NewSize.Width, (int)e.NewSize.Height);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ThemeVariantScope.ActualThemeVariantProperty)
        {
            Overview.Palette = PictureDrawOperation.PaletteOf(this);
            Overview.InvalidateCache();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Overview.Dispose();
    }

    public override void Render(DrawingContext context)
    {
        Overview.Palette = PictureDrawOperation.PaletteOf(this);
        context.Custom(PictureDrawOperation.Record(Bounds.Size, Overview.Render));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        PointerPoint point = e.GetCurrentPoint(this);
        if (point.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
            return;
        e.Pointer.Capture(this);
        Overview.MousePress(point.Position.X);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        PointerPoint point = e.GetCurrentPoint(this);
        if (point.Properties.IsLeftButtonPressed)
            Overview.MouseDrag(point.Position.X);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
    }
}
