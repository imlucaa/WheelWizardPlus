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
            Version = string.Join('.', Assembly.GetExecutingAssembly().GetName().Version?.ToString().Split('.')[..3] ?? ["0.0.0"]),

            RepositoryUrl = new("https://github.com/imlucaa/WheelWizardPlus"),
            DiscordUrl = new("https://discord.gg/vZ7T2wJnsq"),
            SupportUrl = new("https://ko-fi.com/wheelwizard"),
        };
}
