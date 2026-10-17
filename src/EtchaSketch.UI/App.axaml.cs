using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using EtchaSketch.UI.ViewModels;
using EtchaSketch.UI.Views;

namespace EtchaSketch.UI;

public partial class App : Application
{
    /// <summary>
    /// Set by each platform's app before Avalonia starts. Null means this platform has no secure key
    /// storage yet, and the app shows a message instead of opening any data.
    /// </summary>
    public static AppEnvironment? Environment { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
#if DEBUG
        this.AttachDeveloperTools();
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // One view model for the app's lifetime, so the database is opened once even when Android
        // recreates the view after a rotation.
        var main = new MainViewModel(Environment);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow { DataContext = main };
            desktop.Exit += (_, _) => main.Dispose();
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime activity)
        {
            activity.MainViewFactory = () => new MainView { DataContext = main };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = new MainView { DataContext = main };
        }

        base.OnFrameworkInitializationCompleted();
    }
}