using System.Text.Json;

namespace WheelWizard.DiscImages;

/// <summary>
/// The colour a disc had before the line changer replaced it, kept by the disc's path, so it can be put back. Saved as
/// JSON in one setting; anything missing or damaged means nothing is remembered.
/// </summary>
public static class LineColorHistory
{
    public static LineColor? Get(string? saved, string disc) => Read(saved).TryGetValue(Key(disc), out var color) ? color : null;

    /// <summary>Remembers a colour for a disc, or forgets it when <paramref name="color"/> is null.</summary>
    public static string Set(string? saved, string disc, LineColor? color)
    {
        var all = Read(saved);
        if (color is { } value)
            all[Key(disc)] = value;
        else
            all.Remove(Key(disc));
        return JsonSerializer.Serialize(all);
    }

    private static string Key(string disc) => disc.Trim().ToLowerInvariant();

    private static Dictionary<string, LineColor> Read(string? saved)
    {
        try
        {
            var all = string.IsNullOrWhiteSpace(saved) ? null : JsonSerializer.Deserialize<Dictionary<string, int>>(saved);
            return all?.Where(pair => Enum.IsDefined(typeof(LineColor), pair.Value))
                    .ToDictionary(pair => pair.Key, pair => (LineColor)pair.Value) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
