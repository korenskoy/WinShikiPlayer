using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using WinShikiPlayer.Core;

namespace WinShikiPlayer.Controls;

public sealed class ChapterScrubber : Grid
{
    private readonly Canvas _canvas = new() { IsHitTestVisible = false };
    private bool _dragging;
    private double _duration, _position;
    private List<PlayerChapter> _chapters = [];
    private readonly SolidColorBrush _accent = new(Windows.UI.Color.FromArgb(255, 255, 77, 94));
    public event EventHandler<double>? SeekRequested;
    public double Duration => _duration;
    public double Position => _position;
    public bool IsDragging => _dragging;
    public ChapterScrubber()
    {
        Height = 34; Background = new SolidColorBrush(Colors.Transparent); IsTabStop = true;
        AutomationProperties.SetName(this, "Позиция воспроизведения");
        Children.Add(_canvas); SizeChanged += (_, _) => Draw();
        PointerPressed += (_, e) => { if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return; _dragging = CapturePointer(e.Pointer); Focus(FocusState.Pointer); SeekAt(e); e.Handled = true; };
        PointerMoved += (_, e) => { if (_dragging) SeekAt(e); };
        PointerReleased += (_, e) => { if (!_dragging) return; SeekAt(e); _dragging = false; ReleasePointerCapture(e.Pointer); e.Handled = true; };
        PointerCaptureLost += (_, _) => _dragging = false;
        KeyDown += (_, e) =>
        {
            double? target = e.Key switch { VirtualKey.Left => _position - 10, VirtualKey.Right => _position + 10, VirtualKey.Home => 0, VirtualKey.End => _duration, _ => null };
            if (target is null) return; RequestSeek(target.Value); e.Handled = true;
        };
        GotFocus += (_, _) => { BorderBrush = _accent; BorderThickness = new(0, 0, 0, 1); };
        LostFocus += (_, _) => BorderThickness = new(0);
    }
    public void Update(double duration, double position, SkipRange? opening, SkipRange? ending)
    {
        _duration = double.IsFinite(duration) ? Math.Max(0, duration) : 0;
        if (!_dragging) _position = PlayerTimeline.Clamp(position, _duration);
        _chapters = PlayerTimeline.Chapters(_duration, opening, ending); Draw();
    }
    private void SeekAt(PointerRoutedEventArgs e) => RequestSeek(_duration * Math.Clamp((e.GetCurrentPoint(this).Position.X - 7) / Math.Max(1, ActualWidth - 14), 0, 1));
    internal void RequestSeek(double position)
    {
        var old = _position; _position = PlayerTimeline.Clamp(position, _duration); Draw(); SeekRequested?.Invoke(this, _position);
        (FrameworkElementAutomationPeer.FromElement(this) as ScrubberPeer)?.Changed(old, _position);
    }
    private void Draw()
    {
        _canvas.Children.Clear(); var width = Math.Max(0, ActualWidth - 14); if (width < 1) return;
        var y = _chapters.Count > 0 ? 24d : 16d;
        void Bar(double x, double w, Brush brush) { var b = new Border { Width = Math.Max(0, w), Height = 5, CornerRadius = new(2.5), Background = brush }; Canvas.SetLeft(b, 7 + x); Canvas.SetTop(b, y); _canvas.Children.Add(b); }
        Bar(0, width, new SolidColorBrush(Windows.UI.Color.FromArgb(77, 255, 255, 255)));
        foreach (var chapter in _chapters)
        {
            var x = width * chapter.Start / _duration;
            Bar(x, width * (chapter.End - chapter.Start) / _duration, new SolidColorBrush(Windows.UI.Color.FromArgb(115, 255, 255, 255)));
            var label = new TextBlock { Text = chapter.Label, FontSize = 9, CharacterSpacing = 90, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 255, 255, 255)) };
            Canvas.SetLeft(label, Math.Min(7 + x, Math.Max(0, ActualWidth - 24))); _canvas.Children.Add(label);
        }
        var progress = _duration > 0 ? width * _position / _duration : 0;
        Bar(0, progress, _accent);
        var thumb = new Ellipse { Width = 13, Height = 13, Fill = _accent }; Canvas.SetLeft(thumb, progress + 0.5); Canvas.SetTop(thumb, y - 4); _canvas.Children.Add(thumb);
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new ScrubberPeer(this);
    private sealed class ScrubberPeer(ChapterScrubber owner) : FrameworkElementAutomationPeer(owner), IRangeValueProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
        protected override string GetClassNameCore() => nameof(ChapterScrubber);
        protected override object GetPatternCore(PatternInterface pattern) => pattern == PatternInterface.RangeValue ? this : base.GetPatternCore(pattern);
        public bool IsReadOnly => false; public double LargeChange => 60; public double SmallChange => 10;
        public double Maximum => owner.Duration; public double Minimum => 0; public double Value => owner.Position;
        public void SetValue(double value) => owner.RequestSeek(value);
        internal void Changed(double before, double after) => RaisePropertyChangedEvent(RangeValuePatternIdentifiers.ValueProperty, before, after);
    }
}
