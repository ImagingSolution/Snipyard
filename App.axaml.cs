using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;

namespace Snipyard;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    public void SetTheme(bool isDark)
    {
        RequestedThemeVariant = isDark ? ThemeVariant.Dark : ThemeVariant.Light;

        if (isDark)
        {
            // Apple Dark Mode
            Resources["ToolBarBg"] = new SolidColorBrush(Color.Parse("#2C2C2E"));
            Resources["StatusBarBg"] = new SolidColorBrush(Color.Parse("#1C1C1E"));
            Resources["SurfaceBg"] = new SolidColorBrush(Color.Parse("#000000"));
            Resources["SubtleText"] = new SolidColorBrush(Color.Parse("#98989D"));
            Resources["MeterTrackBg"] = new SolidColorBrush(Color.Parse("#30FFFFFF"));
            Resources["AccentText"] = new SolidColorBrush(Color.Parse("#64A5FF"));
            Resources["DangerText"] = new SolidColorBrush(Color.Parse("#FF6B6B"));
            Resources["DividerColor"] = new SolidColorBrush(Color.Parse("#38383A"));
            Resources["ActivityBarBg"] = new SolidColorBrush(Color.Parse("#1C1C1E"));
            Resources["SidePanelBg"] = new SolidColorBrush(Color.Parse("#2C2C2E"));
        }
        else
        {
            // Apple Light Mode
            Resources["ToolBarBg"] = new SolidColorBrush(Color.Parse("#F2F2F7"));
            Resources["StatusBarBg"] = new SolidColorBrush(Color.Parse("#E5E5EA"));
            Resources["SurfaceBg"] = new SolidColorBrush(Color.Parse("#FFFFFF"));
            Resources["SubtleText"] = new SolidColorBrush(Color.Parse("#54545C"));
            Resources["MeterTrackBg"] = new SolidColorBrush(Color.Parse("#1A000000"));
            Resources["AccentText"] = new SolidColorBrush(Color.Parse("#005AC8"));
            Resources["DangerText"] = new SolidColorBrush(Color.Parse("#BE2323"));
            Resources["DividerColor"] = new SolidColorBrush(Color.Parse("#C6C6C8"));
            Resources["ActivityBarBg"] = new SolidColorBrush(Color.Parse("#E5E5EA"));
            Resources["SidePanelBg"] = new SolidColorBrush(Color.Parse("#F2F2F7"));
        }
    }
}
