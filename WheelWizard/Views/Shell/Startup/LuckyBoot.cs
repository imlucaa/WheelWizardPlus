using System.Runtime.InteropServices;
using System.Text;

namespace WheelWizard.Views.Shell.Startup;

/// <summary>
/// A 1 in 100 start: the splash jumps out with the gold token and a sparkle outline instead of the wheel, and the chest
/// opening sound plays. BWW_LUCKY_BOOT=1 forces it (for testing), BWW_LUCKY_BOOT=0 turns it off.
/// </summary>
public static class LuckyBoot
{
    public const double Chance = 0.01;

    public static bool Roll(Func<double>? random = null) =>
        Environment.GetEnvironmentVariable("BWW_LUCKY_BOOT") switch
        {
            "1" => true,
            "0" => false,
            _ => (random ?? Random.Shared.NextDouble)() < Chance,
        };

    // Windows' own media player call (winmm). NAudio's player shut the whole app down once the sound ended.
    [DllImport("winmm.dll", EntryPoint = "mciSendStringW", CharSet = CharSet.Unicode)]
    private static extern int MciSendString(string command, StringBuilder? returnValue, int returnLength, IntPtr callback);

    /// <summary>Plays the chest sound once. Windows only; any sound problem is ignored.</summary>
    public static void PlaySound()
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            var path = Path.Combine(Path.GetTempPath(), "BetterWheelWizard-lucky-chest.mp3");
            using (var resource = typeof(LuckyBoot).Assembly.GetManifestResourceStream("Sounds/LuckyChest.mp3"))
            {
                if (resource is null)
                    return;
                using var file = File.Create(path);
                resource.CopyTo(file);
            }

            MciSendString("close bwwlucky", null, 0, IntPtr.Zero);
            if (MciSendString($"open \"{path}\" type mpegvideo alias bwwlucky", null, 0, IntPtr.Zero) == 0)
                MciSendString("play bwwlucky", null, 0, IntPtr.Zero);
        }
        catch (Exception)
        {
            // No sound device: the start still works, just silently.
        }
    }
}
