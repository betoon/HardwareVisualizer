using System.Configuration;
using System.Data;
using System.Windows;

namespace HardwareVisualizer;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var splash = new SplashScreen("Assets/splash.png");
        splash.Show(autoClose: false);

        var window = new MainWindow();
        MainWindow = window;
        window.Loaded += (_, _) => splash.Close(TimeSpan.FromMilliseconds(350));
        window.Show();
    }
}
