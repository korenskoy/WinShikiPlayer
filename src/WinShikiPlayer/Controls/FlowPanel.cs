using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace WinShikiPlayer.Controls;

public sealed class FlowPanel : Panel
{
    public double Spacing { get; set; } = 8;
    protected override Size MeasureOverride(Size availableSize)
    {
        double x = 0, y = 0, line = 0, width = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > availableSize.Width) { y += line + Spacing; x = 0; line = 0; }
            width = Math.Max(width, x + size.Width); x += size.Width + Spacing; line = Math.Max(line, size.Height);
        }
        return new Size(width, y + line);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, line = 0;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > finalSize.Width) { y += line + Spacing; x = 0; line = 0; }
            child.Arrange(new Rect(x, y, size.Width, size.Height));
            x += size.Width + Spacing; line = Math.Max(line, size.Height);
        }
        return finalSize;
    }
}

public sealed class PosterPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 180;
        var size = new Size(width, width * 1.5);
        foreach (var child in Children) child.Measure(size);
        return size;
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children) child.Arrange(new Rect(new Point(), finalSize));
        return finalSize;
    }
}
