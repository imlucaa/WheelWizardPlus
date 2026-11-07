using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using WheelWizard.Settings;

namespace WheelWizard.LoungeStats;

/// <summary>
/// Whose RR Lounge stats show and how they look: which numbers, the style (a stats card, or the compact Mario Kart Wii
/// HUD look), size, place on the OBS page, background and colours. Shared by the settings preview, the window and the OBS
/// page, so a change shows on all of them at once. Only the values that differ from the defaults are saved.
/// </summary>
public sealed class LoungeStatsConfig
{
    private static readonly JsonObject Defaults = JsonNode
        .Parse(
            """
            {
              "player": { "name": "", "id": "", "ladder": "rr" },
              "data": { "refreshSeconds": 60 },
              "show": { "component": true, "rank": true, "badge": true, "flag": true, "tag": true, "mmr": true, "wins": true, "losses": true,
                        "place": false, "labels": true },
              "layout": { "style": "card", "direction": "vertical", "scale": 1, "spacing": 10, "textSize": 1, "opacity": 1,
                          "anchor": "top left", "x": 24, "y": 24 },
              "background": { "mode": "solid", "color": "#0E1220", "opacity": 0.85, "radius": 12 },
              "colors": { "text": "#FFFFFF", "label": "#9AA3BB", "mmr": "#FFFFFF", "divisionColor": true, "rank": "#FFE88F",
                          "wins": "#5EF0A5", "losses": "#FF6D7E" },
              "hud": { "fill": "#F2F20A", "fillBottom": "#B4B800", "outline": "#0A0A0A", "depth": "#77751C", "depthSize": 4,
                       "emblem": true, "badgeOutline": 9, "emblemDivision": true, "emblemColor": "#E5173F", "emblemDark": "#2B0408", "emblemEdge": "#000000",
                       "emblemSymbol": "#FF7896" },
              "effects": { "animations": true }
            }
            """
        )!
        .AsObject();

    public static readonly string[] Ladders = ["rr", "ct", "tt"];
    public static readonly string[] Styles = ["card", "hud"];
    public static readonly string[] Directions = ["vertical", "horizontal"];
    public static readonly string[] BackgroundModes = ["transparent", "solid", "blurred"];
    public static readonly string[] Anchors =
    [
        "top left",
        "top center",
        "top right",
        "center",
        "bottom left",
        "bottom center",
        "bottom right",
    ];

    /// <summary>The refresh can't be set faster than this, so the site is never asked too often.</summary>
    public const int MinRefreshSeconds = 30;

    public const int MaxRefreshSeconds = 600;

    private readonly ISettingsManager _settings;
    private JsonObject _current;

    public LoungeStatsConfig(ISettingsManager settings)
    {
        _settings = settings;
        _current = Load(settings);
    }

    /// <summary>Raised after any value changed, so every view can redraw.</summary>
    public event Action? Changed;

    private static JsonObject Load(ISettingsManager settings)
    {
        var merged = (JsonObject)Defaults.DeepClone();
        try
        {
            var saved = settings.Get<string>(settings.LOUNGE_STATS_CONFIG);
            if (!string.IsNullOrWhiteSpace(saved) && JsonNode.Parse(saved) is JsonObject patch)
                Merge(merged, patch);
        }
        catch (JsonException) { }

        return merged;
    }

    private static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var (key, value) in patch)
        {
            if (value is JsonObject child && target[key] is JsonObject existing)
                Merge(existing, child);
            else if (target[key] is { } expected && value is not null && SameKind(expected, value))
                target[key] = value.DeepClone();
        }
    }

    // An old or edited setting with a wrong type (text where a number belongs) is skipped instead of breaking a view later.
    private static bool SameKind(JsonNode expected, JsonNode actual)
    {
        static int Group(JsonValueKind kind) => kind is JsonValueKind.True or JsonValueKind.False ? 1 : (int)kind;
        return Group(expected.GetValueKind()) == Group(actual.GetValueKind());
    }

    private static (JsonObject Parent, string Key) Resolve(JsonObject root, string path)
    {
        var parts = path.Split('.');
        var node = root;
        for (var i = 0; i < parts.Length - 1; i++)
            node = node[parts[i]]!.AsObject();
        return (node, parts[^1]);
    }

    public bool GetBool(string path) => Resolve(_current, path) is var (p, k) && p[k]!.GetValue<bool>();

    // Read through the text form: a number set from code as an int would throw when asked for a double.
    public double GetNumber(string path) =>
        Resolve(_current, path) is var (p, k)
        && double.TryParse(p[k]!.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : 0;

    public string GetString(string path) => Resolve(_current, path) is var (p, k) ? p[k]!.GetValue<string>() : string.Empty;

    public void Set(string path, JsonNode? value)
    {
        var (parent, key) = Resolve(_current, path);
        parent[key] = value;
        Save();
        Changed?.Invoke();
    }

    /// <summary>Puts every look option back to its default. The player stays.</summary>
    public void Reset()
    {
        var player = _current["player"]!.DeepClone();
        _current = (JsonObject)Defaults.DeepClone();
        _current["player"] = player;
        Save();
        Changed?.Invoke();
    }

    /// <summary>
    /// The compact race-HUD look: the emblem and the MMR in big HUD digits, low on the left of the OBS page, with nothing
    /// else and no background, like the number under the place on the Mario Kart Wii HUD.
    /// </summary>
    public static readonly (string Path, JsonNode Value)[] CompactHud =
    [
        ("layout.style", "hud"),
        ("layout.direction", "horizontal"),
        ("layout.anchor", "bottom left"),
        ("background.mode", "transparent"),
        ("show.tag", false),
        ("show.flag", false),
        ("show.wins", false),
        ("show.losses", false),
        ("show.place", false),
        ("hud.emblem", true),
    ];

    /// <summary>Puts the look back to the defaults (the player stays) and then sets these values, as one change.</summary>
    public void ApplyLook(IEnumerable<(string Path, JsonNode Value)> values)
    {
        var player = _current["player"]!.DeepClone();
        var data = _current["data"]!.DeepClone();
        _current = (JsonObject)Defaults.DeepClone();
        _current["player"] = player;
        _current["data"] = data;
        foreach (var (path, value) in values)
        {
            var (parent, key) = Resolve(_current, path);
            if (parent[key] is { } expected && SameKind(expected, value))
                parent[key] = value.DeepClone();
        }

        Save();
        Changed?.Invoke();
    }

    /// <summary>Picks whose stats show. The id is the site's own, so a later name change on the site is followed.</summary>
    public void SetPlayer(string name, string id)
    {
        if (PlayerName == name && PlayerId == id)
            return;
        var player = _current["player"]!.AsObject();
        player["name"] = name;
        player["id"] = id;
        Save();
        Changed?.Invoke();
    }

    public string PlayerName => GetString("player.name");
    public string PlayerId => GetString("player.id");

    /// <summary>"rr", "ct" or "tt", the value the site takes for its ladders.</summary>
    public string Ladder => GetString("player.ladder") is var ladder && Ladders.Contains(ladder) ? ladder : "rr";

    /// <summary>How long between reads of the site, kept between 30 seconds and 10 minutes.</summary>
    public TimeSpan RefreshInterval =>
        TimeSpan.FromSeconds(Math.Clamp(GetNumber("data.refreshSeconds"), MinRefreshSeconds, MaxRefreshSeconds));

    // Only what differs from the defaults is kept, so a new default reaches everyone who never changed that value.
    private void Save()
    {
        var changes = new JsonObject();
        Diff(Defaults, _current, changes);
        _settings.Set(_settings.LOUNGE_STATS_CONFIG, changes.Count == 0 ? string.Empty : changes.ToJsonString());
    }

    private static void Diff(JsonObject defaults, JsonObject current, JsonObject into)
    {
        foreach (var (key, value) in current)
        {
            if (value is JsonObject child && defaults[key] is JsonObject baseline)
            {
                var nested = new JsonObject();
                Diff(baseline, child, nested);
                if (nested.Count > 0)
                    into[key] = nested;
            }
            else if (value is not null && defaults[key]?.ToJsonString() != value.ToJsonString())
            {
                into[key] = value.DeepClone();
            }
        }
    }

    public static bool IsHex(string text) => text.Length is 7 or 4 && text[0] == '#' && text.Skip(1).All(Uri.IsHexDigit);

    /// <summary>"#RRGGBB" from "#RGB" or "#RRGGBB", upper case.</summary>
    public static string Normalize(string hex) =>
        hex.Length == 4 ? $"#{hex[1]}{hex[1]}{hex[2]}{hex[2]}{hex[3]}{hex[3]}".ToUpperInvariant() : hex.ToUpperInvariant();

    /// <summary>The colour at this path when it is a plain hex colour, else the fallback (nothing else reaches CSS).</summary>
    public string Color(string path, string fallback) => GetString(path) is var value && IsHex(value) ? Normalize(value) : fallback;

    /// <summary>The colour the rank is written in: the site's colour for the division, or the one picked.</summary>
    public string RankColor(LoungeStatsSnapshot? stats) =>
        GetBool("colors.divisionColor") && stats?.DivisionColor is { } site && IsHex(site)
            ? Normalize(site)
            : Color("colors.rank", "#FFE88F");
}
