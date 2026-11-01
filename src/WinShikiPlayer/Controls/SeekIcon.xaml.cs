using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinShikiPlayer.Controls;

public sealed partial class SeekIcon : UserControl
{
    public static readonly DependencyProperty ForwardProperty = DependencyProperty.Register(
        nameof(Forward), typeof(bool), typeof(SeekIcon), new PropertyMetadata(false, Changed));
    public static readonly DependencyProperty PlusProperty = DependencyProperty.Register(
        nameof(Plus), typeof(bool), typeof(SeekIcon), new PropertyMetadata(false, Changed));
    public bool Forward { get => (bool)GetValue(ForwardProperty); set => SetValue(ForwardProperty, value); }
    public bool Plus { get => (bool)GetValue(PlusProperty); set => SetValue(PlusProperty, value); }
    public SeekIcon() { InitializeComponent(); UpdateIcon(); }
    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs _) => ((SeekIcon)sender).UpdateIcon();
    private void UpdateIcon()
    {
        if (Direction is null) return;
        Direction.ScaleX = Forward ? -1 : 1;
        Seconds.Visibility = Plus ? Visibility.Collapsed : Visibility.Visible;
        PlusSign.Visibility = Plus ? Visibility.Visible : Visibility.Collapsed;
    }
}
