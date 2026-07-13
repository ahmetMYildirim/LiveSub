using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace PsGameTranslator.App.Controls;

public partial class SecureApiKeyBox : UserControl
{
    private bool _syncing;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(SecureApiKeyBox),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    public SecureApiKeyBox() => InitializeComponent();

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SecureApiKeyBox)d).SyncBoxes(e.NewValue as string ?? string.Empty);

    private void SyncBoxes(string value)
    {
        if (_syncing) return;
        _syncing = true;
        MaskedBox.Password = value;
        VisibleBox.Text = value;
        _syncing = false;
    }

    private void MaskedBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_syncing) Value = MaskedBox.Password;
    }

    private void VisibleBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_syncing) Value = VisibleBox.Text;
    }

    private void Reveal_OnChanged(object sender, RoutedEventArgs e)
    {
        var button = (ToggleButton)sender;
        var reveal = button.IsChecked == true;
        MaskedBox.Visibility = reveal ? Visibility.Collapsed : Visibility.Visible;
        VisibleBox.Visibility = reveal ? Visibility.Visible : Visibility.Collapsed;
        button.Content = reveal ? "Gizle" : "Göster";
    }
}
