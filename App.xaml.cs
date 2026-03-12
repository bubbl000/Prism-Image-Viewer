using System.Windows;

namespace ImageViewer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        LocalizationManager.Apply(AppSettings.Current.Language);
    }
}

