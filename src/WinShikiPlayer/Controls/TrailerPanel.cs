using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace WinShikiPlayer.Controls;

// The original TrailersSection uses five equal columns, 16:9 cards and 10 px gaps.
public sealed class TrailerPanel : Panel
{
    private const int Columns = 5;
    private const double Gap = 10;
    private static double CardWidth(double width) => Math.Max(1, (width - Gap * (Columns - 1)) / Columns);
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 900;
        var card = new Size(CardWidth(width), CardWidth(width) * 9 / 16);
        foreach (var child in Children) child.Measure(card);
        var rows = (Children.Count + Columns - 1) / Columns;
        return new(width, rows == 0 ? 0 : rows * card.Height + (rows - 1) * Gap);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = CardWidth(finalSize.Width); var height = width * 9 / 16;
        for (var i = 0; i < Children.Count; i++) Children[i].Arrange(new Rect(i % Columns * (width + Gap), i / Columns * (height + Gap), width, height));
        return finalSize;
    }
}
