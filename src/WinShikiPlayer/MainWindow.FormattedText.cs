using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media.Imaging;
using WinShikiPlayer.Core;

namespace WinShikiPlayer;

public sealed partial class MainWindow
{
    private UIElement FormattedText(string? raw, int depth = 0)
    {
        raw ??= "";
        if (raw.Length > 100000) raw = raw[..100000];
        var panel = new StackPanel { Spacing = 10, MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Left };
        var blocks = depth < 5 ? Regex.Matches(raw, @"\[(?<tag>spoiler|quote)(?:=(?<title>[^\]]+))?\](?<body>[\s\S]*?)\[/\k<tag>\]|\[img\](?<image>[^\[]+)\[/img\]", RegexOptions.IgnoreCase) : Regex.Matches("", "x");
        var offset = 0;
        foreach (Match block in blocks)
        {
            if (block.Index > offset) panel.Children.Add(FormattedParagraph(raw[offset..block.Index]));
            if (block.Groups["image"].Success)
            {
                if (RemoteUri(block.Groups["image"].Value) is { } uri) panel.Children.Add(new Image { Source = new BitmapImage(uri), MaxHeight = 400, HorizontalAlignment = HorizontalAlignment.Left });
            }
            else if (block.Groups["tag"].Value.Equals("spoiler", StringComparison.OrdinalIgnoreCase))
                panel.Children.Add(new Expander { Header = block.Groups["title"].Success ? block.Groups["title"].Value : "Спойлер", Content = FormattedText(block.Groups["body"].Value, depth + 1), HorizontalAlignment = HorizontalAlignment.Stretch });
            else panel.Children.Add(Surface(FormattedText(block.Groups["body"].Value, depth + 1)));
            offset = block.Index + block.Length;
        }
        if (offset < raw.Length) panel.Children.Add(FormattedParagraph(raw[offset..]));
        return panel;
    }
    private RichTextBlock FormattedParagraph(string text)
    {
        var control = new RichTextBlock { IsTextSelectionEnabled = true, FontSize = 15, LineHeight = 25, TextWrapping = TextWrapping.Wrap };
        var paragraph = new Paragraph();
        var links = Regex.Matches(text, @"\[url=(?<url>[^\]]+)\](?<text>[\s\S]*?)\[/url\]|\[(?<tag>b|i|u|s)\](?<body>[\s\S]*?)\[/\k<tag>\]", RegexOptions.IgnoreCase);
        var offset = 0;
        foreach (Match match in links)
        {
            if (match.Index > offset) paragraph.Inlines.Add(new Run { Text = TextFormatting.Plain(text[offset..match.Index]) });
            if (match.Groups["url"].Success && RemoteUri(match.Groups["url"].Value) is { } uri)
            {
                var link = new Hyperlink(); link.Inlines.Add(new Run { Text = TextFormatting.Plain(match.Groups["text"].Value) });
                link.Click += (_, _) => OpenBrowser(uri); paragraph.Inlines.Add(link);
            }
            else
            {
                Span span = match.Groups["tag"].Value.ToLowerInvariant() switch { "b" => new Bold(), "i" => new Italic(), "u" => new Underline(), _ => new Span() };
                span.Inlines.Add(new Run { Text = TextFormatting.Plain(match.Groups["body"].Value) }); paragraph.Inlines.Add(span);
            }
            offset = match.Index + match.Length;
        }
        if (offset < text.Length) paragraph.Inlines.Add(new Run { Text = TextFormatting.Plain(text[offset..]) });
        control.Blocks.Add(paragraph); return control;
    }
}
