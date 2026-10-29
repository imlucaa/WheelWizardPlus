using System.Text.Json;
using System.Text.Json.Nodes;
using WheelWizard.Settings;

namespace WheelWizard.NowPlaying;

/// <summary>
/// How the Now playing card looks: its style, size, which parts show and the colours. Shared by the window, the settings
/// preview and the OBS page, so one change shows on all of them. Only the values that differ from the defaults are saved.
/// </summary>
public sealed class NowPlayingConfig
{
    private static readonly JsonObject Defaults = JsonNode
        .Parse(
            """
            {
              "layout": { "style": "classic", "coverSide": "left", "coverShape": "square", "coverSize": 90, "width": 360, "scale": 1, "radius": 10, "gap": 8, "textSize": 1, "align": "left",
                         "fontFamily": "default", "titleWeight": "bold", "uppercase": false, "barCount": 22, "progressHeight": 5, "progressPosition": "bottom" },
              "effects": { "glow": true, "spin": false, "borderWidth": 0, "borderColor": "#FFFFFF", "textShadow": false, "cardShadow": false, "gradient": false },
              "show": { "cover": true, "artist": true, "app": true, "time": true, "bars": true, "progress": true, "album": false, "remaining": false, "paused": true },
              "colors": { "source": "cover", "custom": "#3A6DF0", "text": "#FFFFFF", "cardOpacity": 0.92, "progress": "#FFFFFF", "bars": "#FFFFFF" },
              "behavior": { "hideWhenNotPlaying": true }
            }
            """
        )!
        .AsObject();

    public static readonly string[] Styles = ["classic", "compact", "text", "vertical", "strip"];
    public static readonly string[] CoverShapes = ["square", "circle"];
    public static readonly string[] Aligns = ["left", "center"];
    public static readonly string[] FontFamilies =
    [
        "default",
        "Segoe UI",
        "Arial",
        "Verdana",
        "Trebuchet MS",
        "Georgia",
        "Consolas",
        "Impact",
    ];
    public static readonly string[] TitleWeights = ["normal", "semibold", "bold", "black"];
    public static readonly string[] ProgressPositions = ["bottom", "top"];
    public static readonly string[] CoverSides = ["left", "right"];
    public static readonly string[] ColorSources = ["cover", "custom"];

    private readonly ISettingsManager _settings;
    private JsonObject _current;

    public NowPlayingConfig(ISettingsManager settings)
    {
        _settings = settings;
        _current = Load(settings);
    }

    /// <summary>Raised after any value changed, so every card can redraw.</summary>
    public event Action? Changed;

    private static JsonObject Load(ISettingsManager settings)
    {
        var merged = (JsonObject)Defaults.DeepClone();
        try
        {
            var saved = settings.Get<string>(settings.NOW_PLAYING_CONFIG);
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

    // An old or edited setting with a wrong type (text where a number belongs) is skipped instead of breaking a card later.
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
        && double.TryParse(
            p[k]!.ToJsonString(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var number
        )
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

    public void Reset()
    {
        _current = (JsonObject)Defaults.DeepClone();
        Save();
        Changed?.Invoke();
    }

    // Only what differs from the defaults is kept, so a new default reaches everyone who never changed that value.
    private void Save()
    {
        var changes = new JsonObject();
        Diff(Defaults, _current, changes);
        _settings.Set(_settings.NOW_PLAYING_CONFIG, changes.Count == 0 ? string.Empty : changes.ToJsonString());
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

    /// <summary>The colour of the cards for this cover: its main colour, or the one picked.</summary>
    public string CardColor(byte[]? cover) =>
        GetString("colors.source") == "custom" && IsHex(GetString("colors.custom"))
            ? Normalize(GetString("colors.custom"))
            : CoverColor.From(cover);

    public static bool IsHex(string text) => text.Length is 7 or 4 && text[0] == '#' && text.Skip(1).All(Uri.IsHexDigit);

    /// <summary>"#RRGGBB" from "#RGB" or "#RRGGBB", upper case.</summary>
    public static string Normalize(string hex) =>
        hex.Length == 4 ? $"#{hex[1]}{hex[1]}{hex[2]}{hex[2]}{hex[3]}{hex[3]}".ToUpperInvariant() : hex.ToUpperInvariant();

    /// <summary>The line under the title: the artist and the album, each only when its option is on, joined with a dot.</summary>
    public string ArtistLine(NowPlayingInfo info)
    {
        var parts = new List<string>();
        if (GetBool("show.artist") && info.Artist.Length > 0)
            parts.Add(info.Artist);
        if (GetBool("show.album") && info.Album.Length > 0)
            parts.Add(info.Album);
        return string.Join(" · ", parts);
    }

    /// <summary>True when the card should fade out: the option to hide it is on and nothing is playing.</summary>
    public bool IsHidden(NowPlayingInfo? info) => GetBool("behavior.hideWhenNotPlaying") && info is not { IsPlaying: true };
}
