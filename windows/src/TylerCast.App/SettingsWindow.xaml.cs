using System.Reflection;
using System.Windows;

namespace TylerCast.App;

public partial class SettingsWindow : Window
{
    public SettingsWindow(AppModel model)
    {
        InitializeComponent();
        DataContext = model;
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        Version.Text = $"TylerCast {v?.ToString(3)}";
    }

    private void Log_Click(object sender, RoutedEventArgs e) => AppModel.OpenLog();
}
