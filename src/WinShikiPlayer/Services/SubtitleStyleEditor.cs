using Microsoft.UI.Xaml.Controls;
using WinShikiPlayer.Core;

namespace WinShikiPlayer.Services;

internal sealed class SubtitleStyleEditor
{
    public StackPanel Panel { get; } = new() { Spacing = 12 };
    private readonly ComboBox _weight, _background;
    private readonly TextBox _text, _outline;
    private readonly NumberBox _width, _opacity, _maxLines;
    private readonly ToggleSwitch _shadow;
    public SubtitleStyleEditor(AppSettings settings)
    {
        _weight = new() { Header = "Насыщенность", ItemsSource = new[] { "regular", "medium", "semibold", "bold", "heavy" }, SelectedItem = settings.SubtitleFontWeight };
        _text = new() { Header = "Цвет текста · #RRGGBB", Text = settings.SubtitleTextColor };
        _outline = new() { Header = "Цвет обводки · #RRGGBB", Text = settings.SubtitleOutlineColor };
        _width = new() { Header = "Толщина обводки", Value = settings.SubtitleOutlineWidth, Minimum = 0, Maximum = 8, SmallChange = 0.5 };
        _shadow = new() { Header = "Тень текста", IsOn = settings.SubtitleShadowEnabled };
        _background = new() { Header = "Фон субтитров", ItemsSource = new[] { "none", "shadow", "box" }, SelectedItem = settings.SubtitleBackgroundStyle };
        _opacity = new() { Header = "Непрозрачность фона · 0–1", Value = settings.SubtitleBackgroundOpacity, Minimum = 0, Maximum = 1, SmallChange = 0.05 };
        _maxLines = new() { Header = "Максимум строк", Value = settings.SubtitleMaxLines, Minimum = 1, Maximum = 10 };
        foreach (var control in new Microsoft.UI.Xaml.UIElement[] { _weight, _text, _outline, _width, _shadow, _background, _opacity, _maxLines }) Panel.Children.Add(control);
    }
    public void ReadInto(AppSettings settings)
    {
        _ = SubtitleParser.AssColor(_text.Text); _ = SubtitleParser.AssColor(_outline.Text);
        if (!double.IsFinite(_width.Value) || !double.IsFinite(_opacity.Value) || !double.IsFinite(_maxLines.Value))
            throw new ArgumentException("Проверьте числовые параметры субтитров.");
        settings.SubtitleFontWeight = _weight.SelectedItem as string ?? "semibold";
        settings.SubtitleTextColor = _text.Text; settings.SubtitleOutlineColor = _outline.Text;
        settings.SubtitleOutlineWidth = Math.Clamp(_width.Value, 0, 8); settings.SubtitleShadowEnabled = _shadow.IsOn;
        settings.SubtitleBackgroundStyle = _background.SelectedItem as string ?? "shadow";
        settings.SubtitleBackgroundOpacity = Math.Clamp(_opacity.Value, 0, 1); settings.SubtitleMaxLines = Math.Clamp((int)_maxLines.Value, 1, 10);
    }
}
