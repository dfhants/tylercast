using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace TylerCast.App;

public partial class PanelView : UserControl
{
    /// <summary>"More settings" was clicked (the tray opens the settings window).</summary>
    public event Action? SettingsRequested;

    public PanelView() => InitializeComponent();

    private AppModel Model => (AppModel)DataContext;
    private static TvItem Tv(object sender) => (TvItem)((FrameworkElement)sender).DataContext;

    private void Row_Click(object sender, RoutedEventArgs e) => Model.Toggle(Tv(sender));
    private void Cast_Click(object sender, RoutedEventArgs e) => Model.Cast(Tv(sender));
    private void Disconnect_Click(object sender, RoutedEventArgs e) => Model.Disconnect();
    private void Fix_Click(object sender, RoutedEventArgs e) => AppModel.FixWarning();
    private void Refresh_Click(object sender, RoutedEventArgs e) => Model.Search();
    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
}

/// <summary>true → false and back, for IsEnabled bindings.</summary>
public sealed class Negate : IValueConverter
{
    public static readonly Negate Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
