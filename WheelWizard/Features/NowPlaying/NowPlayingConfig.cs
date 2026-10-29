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

    /// <summary>
    /// Takes a preset: every value goes back to its default, then the preset's values are set. One save and one change
    /// notice for the whole preset. A value the config does not know, or of the wrong kind, is skipped.
    /// </summary>
    public void Apply(IEnumerable<KeyValuePair<string, JsonNode>> values)
    {
        _current = WithValues(values);
        Save();
        Changed?.Invoke();
    }

    /// <summary>True when the look is exactly this preset: the defaults with only its values changed.</summary>
    public bool Matches(IEnumerable<KeyValuePair<string, JsonNode>> values) => ChangesOf(WithValues(values)) == ChangesOf(_current);

    private static JsonObject WithValues(IEnumerable<KeyValuePair<string, JsonNode>> values)
    {
        var look = (JsonObject)Defaults.DeepClone();
        foreach (var (path, value) in values)
        {
            try
            {
                var (parent, key) = Resolve(look, path);
                if (parent[key] is { } expected && SameKind(expected, value))
                    parent[key] = value.DeepClone();
            }
            catch (NullReferenceException) { }
        }

        return look;
    }

    private static string ChangesOf(JsonObject look)
    {
        var changes = new JsonObject();
        Diff(Defaults, look, changes);
        return changes.ToJsonString();
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

    #region Saved looks and files

    private const string FileFormat = "wheel-wizard-now-playing";

    /// <summary>The most looks you can keep.</summary>
    public const int MaxSavedLooks = 24;

    /// <summary>Raised when a look is saved or deleted.</summary>
    public event Action? LooksChanged;

    /// <summary>Only what differs from the defaults, as nested JSON: the look in its smallest form.</summary>
    public JsonObject CurrentChanges()
    {
        var changes = new JsonObject();
        Diff(Defaults, _current, changes);
        return changes;
    }

    /// <summary>The nested values as "group.name" paths with their values.</summary>
    public static IEnumerable<KeyValuePair<string, JsonNode>> Flatten(JsonObject values, string prefix = "")
    {
        foreach (var (key, value) in values)
        {
            var path = prefix.Length == 0 ? key : $"{prefix}.{key}";
            if (value is JsonObject child)
            {
                foreach (var pair in Flatten(child, path))
                    yield return pair;
            }
            else if (value is not null)
            {
                yield return new KeyValuePair<string, JsonNode>(path, value.DeepClone());
            }
        }
    }

    private JsonObject ReadLooks()
    {
        try
        {
            var saved = _settings.Get<string>(_settings.NOW_PLAYING_LOOKS);
            if (!string.IsNullOrWhiteSpace(saved) && JsonNode.Parse(saved) is JsonObject all)
                return all;
        }
        catch (JsonException) { }

        return [];
    }

    /// <summary>The looks you saved, in the order you saved them.</summary>
    public IReadOnlyList<NowPlayingPreset> SavedLooks() =>
        ReadLooks()
            .Where(pair => pair.Value is JsonObject)
            .Select(pair => new NowPlayingPreset(pair.Key, "Your saved look.", Flatten((JsonObject)pair.Value!).ToList()))
            .ToList();

    /// <summary>Keeps the current look under a name (a name that is taken is replaced). False for a bad name or too many looks.</summary>
    public bool SaveLook(string name)
    {
        name = name.Trim();
        var all = ReadLooks();
        if (name.Length is 0 or > 30 || (!all.ContainsKey(name) && all.Count >= MaxSavedLooks))
            return false;

        all[name] = CurrentChanges();
        _settings.Set(_settings.NOW_PLAYING_LOOKS, all.ToJsonString());
        LooksChanged?.Invoke();
        return true;
    }

    public void DeleteLook(string name)
    {
        var all = ReadLooks();
        if (!all.Remove(name))
            return;

        _settings.Set(_settings.NOW_PLAYING_LOOKS, all.Count == 0 ? string.Empty : all.ToJsonString());
        LooksChanged?.Invoke();
    }

    /// <summary>The current look as a small file others can import.</summary>
    public string Export() =>
        new JsonObject { ["format"] = FileFormat, ["look"] = CurrentChanges() }.ToJsonString(
            new JsonSerializerOptions { WriteIndented = true }
        );

    /// <summary>Takes a look from a file made by <see cref="Export"/>. False when it is not one.</summary>
    public bool TryImport(string json)
    {
        try
        {
            if (
                JsonNode.Parse(json) is not JsonObject file
                || file["format"]?.GetValue<string>() != FileFormat
                || file["look"] is not JsonObject look
            )
                return false;

            Apply(Flatten(look));
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    #endregion

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
