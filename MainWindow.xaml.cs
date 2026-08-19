using AEPiLightingSystem.Views;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace AEPiLightingSystem;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();

        ApplicationThemeManager.Apply(this);
        SystemThemeWatcher.Watch(this);

        Loaded += (_, _) => RootNavigation.Navigate(typeof(HomePage));
    }
}
