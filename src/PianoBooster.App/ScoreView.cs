// The practice view of PianoBooster in Avalonia: the frame pacing of CGLView (one frame for
// each refresh of the screen, PianoBooster 1.3.8) with the drawing of PianoBooster.Rendering.
// Part of the .NET port of PianoBooster. GPLv3. It was in tools/ScoreWindow in phase 3; that
// tool still uses it (it links this file).
//
// Each frame (TopLevel.RequestAnimationFrame, in the thread of the interface):
//   1. the event bits of the conductor (CGLView's m_eventBits),
//   2. holding the lock of the song: resizeGL() if the size changed, then PrepareFrame(), which
//      calls UpdateMidiTask() (as paintGL() does) and moves the score on (DrawScrollingSymbols(true)),
//   3. the photo is given to the render thread of Avalonia, which draws it with Skia (without the lock).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using PianoBooster.Core;
using PianoBooster.Midi;
using PianoBooster.Rendering;
using SkiaSharp;

namespace PianoBooster.App;

/// <summary>Times (ms) of something that happens many times, for the measurements.</summary>
public sealed class TimeStats
{
    private readonly List<double> _values = new();
    private readonly object _lock = new();

    public void Add(double ms)
    {
        lock (_lock)
            _values.Add(ms);
    }

    public void Clear()
    {
        lock (_lock)
            _values.Clear();
    }

    public override string ToString()
    {
        lock (_lock)
        {
            if (_values.Count == 0)
                return "-";
            var sorted = new List<double>(_values);
            sorted.Sort();
            double mean = 0;
            foreach (double v in sorted)
                mean += v;
            mean /= sorted.Count;
            double P(double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];
            return FormattableString.Invariant($"n {sorted.Count}, mean {mean:0.000} ms, median {P(0.5):0.000}, 99% {P(0.99):0.000}, max {sorted[^1]:0.000}");
        }
    }
}

/// <summary>The score of the song (the C++ CGLView in its window).</summary>
public sealed class ScoreView : Control
{
    private readonly Song _song;
    private readonly Score _score;
    private readonly IPianoSettings _settings;
    private readonly ConductorThread _conductor;
    private readonly ViewRenderer _renderer = new();
    private readonly object _meterLock = new();
    private FrameSnapshot? _frame;
    private int _width = -1, _height = -1;
    private bool _attached;
    private bool _allowedTimerEvent = true;
    private long _measureStart;

    public ScoreView(Song song, Score score, IPianoSettings settings, ConductorThread conductor)
    {
        _song = song;
        _score = score;
        _settings = settings;
        _conductor = conductor;
        ClipToBounds = true;
        Focusable = true; // Qt::ClickFocus
    }

    /// <summary>The warning shown on the score (CSettings::getWarningMessage()), read every frame.</summary>
    public Func<string>? WarningMessageSource { get; set; }

    /// <summary>CGLView::stopTimerEvent(): the song and the drawing stop (while a dialog is open).
    /// The conductor thread stops; when it starts again the time counts from then, so the time
    /// the dialog was open is not given to the song at once (the C++ gives it).</summary>
    public void StopTimerEvent()
    {
        _allowedTimerEvent = false;
        _conductor.Stop();
    }

    /// <summary>CGLView::startTimerEvent().</summary>
    public void StartTimerEvent()
    {
        if (_allowedTimerEvent)
            return;
        _allowedTimerEvent = true;
        _conductor.Start();
        RequestFrame();
    }

    protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
    }

    /// <summary>The frames as they are prepared, with the time of the refresh (RequestAnimationFrame).</summary>
    public SmoothnessMeter FrameMeter { get; } = new();
    /// <summary>The frames as they are drawn, with the time in the render thread.</summary>
    public SmoothnessMeter RenderMeter { get; } = new();
    /// <summary>How long the lock of the song is held to take the photo.</summary>
    public TimeStats PrepareTimes { get; } = new();
    /// <summary>How long it takes to draw a frame.</summary>
    public TimeStats RenderTimes { get; } = new();
    /// <summary>Called with the event bits of the conductor, once per frame (QtWindow::songEventUpdated).</summary>
    public event Action<uint>? SongEventUpdated;

    public ViewRenderer Renderer => _renderer;

    /// <summary>Only measures from now on.</summary>
    public void RestartMeasurements()
    {
        lock (_meterLock)
        {
            FrameMeter.Clear();
            RenderMeter.Clear();
        }
        PrepareTimes.Clear();
        RenderTimes.Clear();
        _measureStart = Stopwatch.GetTimestamp();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _measureStart = Stopwatch.GetTimestamp();
        RequestFrame();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        base.OnDetachedFromVisualTree(e);
    }

    private void RequestFrame() => TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);

    private void OnFrame(TimeSpan time)
    {
        if (!_attached || !_allowedTimerEvent)
            return; // StartTimerEvent() asks for the frames again

        // CGLView::timerEvent(): the event bits
        uint eventBits = _conductor.TakeEventBits();

        int width = (int)Bounds.Width;
        int height = (int)Bounds.Height;
        var frame = new FrameSnapshot(); // a new one: the render thread may still be drawing the last one
        long start = Stopwatch.GetTimestamp();
        lock (_conductor.SyncRoot)
        {
            if ((eventBits & EventBits.UptoBarReached) != 0)
                _song.PlayFromStartBar();
            if (width != _width || height != _height)
            {
                _width = width;
                _height = height;
                _renderer.Resize(Math.Max(width, 1), Math.Max(height, 1));
            }
            if (WarningMessageSource != null)
                _renderer.WarningMessage = WarningMessageSource();
            _renderer.PrepareFrame(_song, _score, _settings, _conductor.UpdateMidiTask, frame);
        }
        PrepareTimes.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        if (eventBits != 0)
            SongEventUpdated?.Invoke(eventBits);

        lock (_meterLock)
            FrameMeter.Add(time.TotalSeconds, frame.ScrollPosition);
        _frame = frame;
        InvalidateVisual();
        RequestFrame(); // the next frame, at the next refresh of the screen
    }

    public override void Render(DrawingContext context)
    {
        FrameSnapshot? frame = _frame;
        if (frame == null)
            return;
        float scaling = (float)(TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0);
        context.Custom(new ScoreDrawOperation(new Rect(Bounds.Size), this, frame, scaling));
    }

    // Runs in the render thread
    private void RenderFrame(SKCanvas canvas, FrameSnapshot frame, float scaling)
    {
        long start = Stopwatch.GetTimestamp();
        _renderer.Render(canvas, frame, scaling);
        RenderTimes.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        lock (_meterLock)
            RenderMeter.Add(Stopwatch.GetElapsedTime(_measureStart, start).TotalSeconds, frame.ScrollPosition);
    }

    private sealed class ScoreDrawOperation : ICustomDrawOperation
    {
        private readonly ScoreView _view;
        private readonly FrameSnapshot _frame;
        private readonly float _scaling;

        public ScoreDrawOperation(Rect bounds, ScoreView view, FrameSnapshot frame, float scaling)
        {
            Bounds = bounds;
            _view = view;
            _frame = frame;
            _scaling = scaling;
        }

        public Rect Bounds { get; }

        public bool HitTest(Point p) => Bounds.Contains(p);

        public bool Equals(ICustomDrawOperation? other) => false; // a new frame every time

        public void Render(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature == null)
                return; // not drawn with Skia
            using ISkiaSharpApiLease lease = leaseFeature.Lease();
            _view.RenderFrame(lease.SkCanvas, _frame, _scaling);
        }

        public void Dispose()
        {
        }
    }
}
