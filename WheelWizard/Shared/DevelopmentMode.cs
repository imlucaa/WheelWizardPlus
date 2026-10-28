namespace WheelWizard.Shared;

public static class DevelopmentMode
{
#if DEBUG
    /// <summary>
    /// Debug builds hide the Dev Tool unless <c>WHEELWIZARD_DEV=1</c> is set, so running one to try the app looks like the
    /// release. Release builds never show it.
    /// </summary>
    public static bool IsEnabled { get; private set; } = Environment.GetEnvironmentVariable("WHEELWIZARD_DEV") == "1";

    public static void Hide() => IsEnabled = false;
#else
    public static bool IsEnabled => false;

    public static void Hide() { }
#endif
}
