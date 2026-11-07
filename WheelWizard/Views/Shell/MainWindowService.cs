using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using WheelWizard.Views.Shell.Startup;

namespace WheelWizard.Views.Shell;

public interface IMainWindowService
{
    Task ShowAsync(IClassicDesktopStyleApplicationLifetime desktop, CancellationToken cancellationToken = default);
}

public sealed class MainWindowService(
    Func<Layout> createWindow,
    TimeProvider time,
    WheelWizard.Settings.ISettingsManager? settings = null,
    WheelWizard.ApplicationData.IApplicationDataLocation? applicationData = null
) : IMainWindowService
{
    /// <summary>Reveals initial content when ready, or after a 10-second recovery limit; shutdown still cancels startup.</summary>
    public async Task ShowAsync(IClassicDesktopStyleApplicationLifetime desktop, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var splash = desktop.MainWindow as Startup.SplashWindow;
        var window = createWindow();
        desktop.MainWindow = window;
        var decorations = window.WindowDecorations;
        var transparency = window.TransparencyLevelHint;
        window.WindowDecorations = WindowDecorations.None;
        window.TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        window.Opacity = 0;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.IsHitTestVisible = false;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        window.Show();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10), time);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
        try
        {
            await window.WaitForInitialContentAsync(waiting.Token);
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            Serilog.Log.Warning("Initial content is still loading after 10 seconds; revealing the main window");
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (splash is { IsLucky: true })
        {
            await splash.HoldAsync(cancellationToken);
            ApplyGoldTheme();
        }
        window.WindowDecorations = decorations;
        window.TransparencyLevelHint = transparency;
        window.Opacity = 1;
        window.ShowInTaskbar = true;
        window.IsHitTestVisible = true;
        window.ShowActivated = true;
        desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
        splash?.Close();
        window.Activate();
    }

    // The lucky start's gold theme: accent, title colour, text and a dark gold background.
    private static readonly WheelWizard.Views.Pages.LauncherThemeFile Gold = new("Golden", "#FFC83D", "#FFD86B", "#FFF1C9", "#1A1206");

    /// <summary>
    /// The lucky start turns the whole launcher gold. The theme you had is saved first in the Theme library as
    /// "Before the gold (date)", so it is one click away.
    /// </summary>
    private void ApplyGoldTheme()
    {
        if (settings is null)
            return;
        try
        {
            var current = new WheelWizard.Views.Pages.LauncherThemeFile(
                $"Before the gold ({DateTime.Now:yyyy-MM-dd HH.mm})",
                settings.Get<string>(settings.LAUNCHER_THEME_COLOR),
                settings.Get<string>(settings.LAUNCHER_TEXT_COLOR),
                settings.Get<string>(settings.LAUNCHER_BODY_TEXT_COLOR),
                settings.Get<string>(settings.LAUNCHER_BACKGROUND_COLOR)
            );
            var alreadyGold =
                string.Equals(current.Accent, Gold.Accent, StringComparison.OrdinalIgnoreCase)
                && string.Equals(current.Background, Gold.Background, StringComparison.OrdinalIgnoreCase);
            if (!alreadyGold && applicationData is not null && current.HasValidNameAndColors())
            {
                var folder = Path.Combine(applicationData.DirectoryPath, "Themes");
                Directory.CreateDirectory(folder);
                File.WriteAllText(
                    Path.Combine(folder, current.Name + ".bwwtheme"),
                    System.Text.Json.JsonSerializer.Serialize(current, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })
                );
            }

            WheelWizard.Settings.Views.SettingsEditing.Set(settings, settings.LAUNCHER_THEME_COLOR, Gold.Accent);
            WheelWizard.Settings.Views.SettingsEditing.Set(settings, settings.LAUNCHER_TEXT_COLOR, Gold.Branding);
            WheelWizard.Settings.Views.SettingsEditing.Set(settings, settings.LAUNCHER_BODY_TEXT_COLOR, Gold.Text);
            WheelWizard.Settings.Views.SettingsEditing.Set(settings, settings.LAUNCHER_BACKGROUND_COLOR, Gold.Background);
        }
        catch (Exception exception)
        {
            Serilog.Log.Warning(exception, "Could not turn the theme gold");
        }
    }
}
