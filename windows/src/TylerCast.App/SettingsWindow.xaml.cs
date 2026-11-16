using System.Reflection;
using System.Windows;

namespace TylerCast.App;

public partial class SettingsWindow : Window
{
    public SettingsWindow(AppModel model)
    {
        InitializeComponent();
        DataContext = model;
        try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/TylerCast.ico")); }
        catch (Exception) { } // only cosmetic (and --screenshot has no resources to hand)
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        Version.Text = $"TylerCast {v?.ToString(3)}";
    }

    private void Log_Click(object sender, RoutedEventArgs e) => AppModel.OpenLog();
}
