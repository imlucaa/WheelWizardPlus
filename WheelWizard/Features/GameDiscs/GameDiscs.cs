using System.Text.Json;
using WheelWizard.Models.Enums;

namespace WheelWizard.GameDiscs;

/// <summary>
/// Your Mario Kart Wii discs, one per region. The launcher still starts one disc at a time (the game location), so choosing
/// a region switches the game location to that region's disc.
/// </summary>
public static class RegionDiscs
{
    public static string? PathFor(string json, MarioKartWiiEnums.Regions region)
    {
        var all = Parse(json);
        return all.TryGetValue(region.ToString(), out var path) && !string.IsNullOrWhiteSpace(path) ? path : null;
    }

    /// <summary>The stored list with this region's disc set, or removed when <paramref name="path"/> is null.</summary>
    public static string WithPath(string json, MarioKartWiiEnums.Regions region, string? path)
    {
        var all = Parse(json);
        if (string.IsNullOrWhiteSpace(path))
            all.Remove(region.ToString());
        else
            all[region.ToString()] = path;
        return JsonSerializer.Serialize(all);
    }

    private static Dictionary<string, string> Parse(string json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json)
                ? new Dictionary<string, string>()
                : JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }
}
