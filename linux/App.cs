using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace OverByte;

public class App : Application
{
    public override void Initialize()
    {
        Name = "OverByte";
        RequestedThemeVariant = ThemeVariant.Light; // same light content area + dark header as the Windows version
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow(desktop.Args?.FirstOrDefault(a => !a.StartsWith("-")));
        base.OnFrameworkInitializationCompleted();
    }
}
