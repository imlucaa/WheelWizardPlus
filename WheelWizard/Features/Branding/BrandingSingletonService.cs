using System.Reflection;

namespace WheelWizard.Branding;

/// <summary>
/// A service that provides branding information for the application.
/// </summary>
public interface IBrandingSingletonService
{
    /// <summary>
    /// The branding information for the application.
    /// </summary>
    Branding Branding { get; }
}

public class BrandingSingletonService : IBrandingSingletonService
{
    public Branding Branding { get; } =
        new()
        {
            DisplayName = "Wheel Wizard+",
            Identifier = "BetterWheelWizard",
            Version = CurrentVersion(),

            RepositoryUrl = new("https://github.com/imlucaa/WheelWizardPlus"),
            DiscordUrl = new("https://discord.gg/vZ7T2wJnsq"),
            SupportUrl = new("https://ko-fi.com/wheelwizard"),
        };

    // The version the build was given, so a prerelease keeps its suffix ("2.6.8-pre.1"); the update check then sees the
    // final 2.6.8 as newer. The build adds "+commit" to it, which is not part of the version.
    private static string CurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
        if (!string.IsNullOrWhiteSpace(informational))
            return informational;

        return string.Join('.', assembly.GetName().Version?.ToString().Split('.')[..3] ?? ["0.0.0"]);
    }
}
