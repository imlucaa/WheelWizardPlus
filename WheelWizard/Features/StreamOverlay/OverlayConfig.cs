using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WheelWizard.Settings;
using WheelWizard.Settings.Views;

namespace WheelWizard.StreamOverlay;

/// <summary>
/// The look of the Rewind VR overlay (a web page by MichaelAccount1, MIT). The page takes everything in the link as a
/// base64url JSON "cfg" of only the values that differ from its defaults, so this keeps the same names and defaults
/// and only ever writes what was changed.
/// </summary>
public sealed class OverlayConfig(ISettingsManager settings)
{
    public static string PageUrl => $"http://127.0.0.1:{DiscordPresence.RoomLinkListener.Port}/rewind-overlay/";

    private static readonly JsonObject Defaults = JsonNode
        .Parse(
            """
            {
              "identity": { "tag": "" },
              "data": { "pollSeconds": 5, "source": "vr" },
              "visibility": {
                "avatar": true, "name": true, "tag": true, "vr": true, "delta": true, "rank": true, "rankDelta": true,
                "connection": false, "room": false, "track": false, "sessionDelta": false, "dailyDelta": false,
                "rankBadge": false, "rankOutline": false, "flag": false
              },
              "avatar": { "background": "gradient", "color1": "#31d4ff", "color2": "#3556ae" },
              "layout": { "scale": 1, "width": 560, "compact": false, "align": "horizontal", "card": true, "look": "badge" },
              "typography": {
                "weight": 800, "textColor": "#ffffff", "mutedColor": "#c9d1e7",
                "vrColor": "#8effc6", "gainColor": "#5ef0a5", "lossColor": "#ff6d7e", "rankColor": "#ffe88f",
                "hudDigits": false, "rankOutlineColor": "#FFFFFF", "rankOutlineWidth": 3, "rankBadgeSize": 100, "rankBadgeGap": 15
              },
              "background": {
                "imageUrl": "", "fit": "cover", "x": 50, "y": 50, "zoom": 1, "blur": 0, "brightness": 0.58, "saturation": 1.15, "contrast": 1.08,
                "overlayColor": "#07111f", "overlayOpacity": 0.38, "glass": 0.26
              },
              "border": {
                "effect": "rainbow", "width": 4, "radius": 38, "speed": 5.5, "glow": true, "glowStrength": 0.65,
                "color1": "#22d3ee", "color2": "#8b5cf6", "color3": "#f97316"
              },
              "animations": { "vr": "count", "rank": "flip", "durationMs": 1100, "celebrateThreshold": 250, "reducedMotion": false }
            }
            """
        )!
        .AsObject();

    private JsonObject _current = Load(settings);

    /// <summary>Raised after any value changed, so the preview can redraw.</summary>
    public event Action? Changed;

    private static JsonObject Load(ISettingsManager settings)
    {
        var merged = (JsonObject)Defaults.DeepClone();
        try
        {
            var saved = settings.Get<string>(settings.OVERLAY_CONFIG);
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

    // A file or old setting with a wrong type (a number where on/off belongs) is skipped instead of breaking the editor later.
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

    #region Export and import

    private const string FileFormat = "wheel-wizard-vr-overlay";

    /// <summary>The current look as a small file others can import.</summary>
    public string Export() =>
        new JsonObject
        {
            ["format"] = FileFormat,
            ["version"] = 1,
            ["look"] = Patch(),
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    /// <summary>Applies a file made by Export, a bare look, or the web Studio's exported profile (unknown parts are ignored).</summary>
    public bool TryImport(string json)
    {
        JsonObject? look;
        try
        {
            look = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return false;
        }

        if (look is null)
            return false;
        if (look["look"] is JsonObject wrapped)
            look = wrapped;
        else if (look["config"] is JsonObject studio)
            look = studio;

        var next = (JsonObject)Defaults.DeepClone();
        Merge(next, look);
        // A picture from a file is only trusted as a web link.
        var image = next["background"]?["imageUrl"]?.GetValue<string>() ?? string.Empty;
        if (image.Length > 0 && !image.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            next["background"]!["imageUrl"] = string.Empty;

        _current = next;
        Save();
        Changed?.Invoke();
        return true;
    }

    #endregion

    #region Presets

    private JsonObject LoadPresets()
    {
        try
        {
            if (JsonNode.Parse(settings.Get<string>(settings.OVERLAY_PRESETS)) is JsonObject presets)
                return presets;
        }
        catch (JsonException) { }
        catch (ArgumentNullException) { }

        return new JsonObject();
    }

    /// <summary>Looks that ship with Wheel Wizard. They cannot be deleted or overwritten.</summary>
    private static readonly Dictionary<string, JsonObject> BuiltIns = BuildBuiltIns();

    // One clean look per launcher theme color: accent border and glow, a dark tint of that color, soft secondary text.
    private static Dictionary<string, JsonObject> BuildBuiltIns()
    {
        var list = new Dictionary<string, JsonObject>
        {
            ["Default"] = [],
            ["Compact"] = JsonNode
                .Parse("""{"layout":{"compact":true,"width":440},"border":{"radius":24,"width":3},"visibility":{"rankDelta":false}}""")!
                .AsObject(),
        };
        foreach (
            var (name, accent, light, dark) in new[]
            {
                ("White", "#FFFFFF", "#FFFFFF", "#000000"),
                ("Red", "#F04444", "#F8DADA", "#260909"),
                ("Orange", "#FF8A3D", "#FFE0CC", "#281006"),
                ("Yellow", "#FFD80D", "#FFF4B8", "#211B02"),
                ("Green", "#25C982", "#D0F5E3", "#062418"),
                ("Cyan", "#20D4E8", "#D0F8FC", "#05252A"),
                ("Blue", "#249AF3", "#DDF2FF", "#071A2D"),
                ("Purple", "#9B6DFF", "#E4D9FF", "#160A2C"),
                ("Pink", "#FF4FA3", "#FFD8EA", "#290817"),
                ("Coral", "#FF6B6B", "#FFE1E1", "#271011"),
                ("Peach", "#FF9F68", "#FFE8D8", "#28160C"),
                ("Mint", "#52D6A8", "#D9F8ED", "#09251D"),
                ("Emerald", "#10B981", "#D1FAE5", "#05251A"),
                ("Teal", "#14B8A6", "#CCFBF1", "#052522"),
                ("Sky", "#38BDF8", "#DDF5FF", "#082330"),
                ("Indigo", "#6366F1", "#E0E1FF", "#11132E"),
                ("Lavender", "#A78BFA", "#EDE7FF", "#1B1230"),
                ("Violet", "#8B5CF6", "#E8DEFF", "#170C2D"),
                ("Rose", "#F43F5E", "#FFE0E6", "#2A0810"),
            }
        )
        {
            list[name] = JsonNode
                .Parse(
                    """{"border":{"effect":"solid","width":3,"glow":true,"glowStrength":0.55,"color1":"ACCENT","color2":"ACCENT","color3":"LIGHT"},"background":{"overlayColor":"DARK","overlayOpacity":0.6},"avatar":{"color1":"ACCENT","color2":"DARK"},"typography":{"mutedColor":"LIGHT"}}"""
                        .Replace("ACCENT", accent)
                        .Replace("LIGHT", light)
                        .Replace("DARK", dark)
                )!
                .AsObject();
        }

        return list;
    }

    public static bool IsBuiltIn(string name) => BuiltIns.ContainsKey(name);

    public IReadOnlyList<string> PresetNames =>
        BuiltIns.Keys.Concat(LoadPresets().Select(p => p.Key).Where(n => !BuiltIns.ContainsKey(n))).ToList();

    /// <summary>The look stored under this name (built-in or saved), for drawing its colors.</summary>
    public JsonObject? PresetPatch(string name) =>
        BuiltIns.TryGetValue(name, out var builtIn) ? builtIn : LoadPresets()[name] as JsonObject;

    /// <summary>Saves the current look under this name (replacing a preset with the same name).</summary>
    public void SavePreset(string name)
    {
        if (BuiltIns.ContainsKey(name))
            return;

        var presets = LoadPresets();
        presets[name] = Patch();
        SettingsEditing.Set(settings, settings.OVERLAY_PRESETS, presets.ToJsonString());
    }

    public bool LoadPreset(string name)
    {
        if (PresetPatch(name) is not { } patch)
            return false;

        _current = (JsonObject)Defaults.DeepClone();
        Merge(_current, patch);
        Save();
        Changed?.Invoke();
        return true;
    }

    public void DeletePreset(string name)
    {
        var presets = LoadPresets();
        if (presets.Remove(name))
            SettingsEditing.Set(settings, settings.OVERLAY_PRESETS, presets.ToJsonString());
    }

    #endregion

    private void Save() => SettingsEditing.Set(settings, settings.OVERLAY_CONFIG, Patch().ToJsonString());

    /// <summary>Only the values that differ from the page's defaults.</summary>
    public JsonObject Patch() => Diff(_current, Defaults) ?? new JsonObject();

    private static JsonObject? Diff(JsonObject current, JsonObject defaults)
    {
        var result = new JsonObject();
        foreach (var (key, value) in current)
        {
            if (value is JsonObject child && defaults[key] is JsonObject baseChild)
            {
                if (Diff(child, baseChild) is { } sub)
                    result[key] = sub;
            }
            else if (!JsonNode.DeepEquals(value, defaults[key]))
            {
                result[key] = value?.DeepClone();
            }
        }

        return result.Count == 0 ? null : result;
    }

    /// <summary>The OBS Browser source link for the given friend code (12 digits).</summary>
    /// <summary>Where the VR overlay page reads its player from when it shows the RR Lounge MMR.</summary>
    public static string MmrDataUrl => $"http://127.0.0.1:{DiscordPresence.RoomLinkListener.Port}/overlay-mmr";

    /// <summary>True when the badge shows the RR Lounge MMR instead of the VR.</summary>
    public bool ShowsMmr => GetString("data.source") == "mmr";

    /// <summary>
    /// The race HUD look, which comes with the "race hud" VR change: no card, the race HUD digits and the game font,
    /// whatever the badge settings say (they are kept).
    /// </summary>
    public bool UsesHudLook => GetString("animations.vr") == "race hud";

    /// <summary>Whether the card (background, border, glow) is drawn.</summary>
    public bool ShowsCard => !UsesHudLook && GetBool("layout.card");

    /// <summary>Whether the number is drawn in the race HUD digits.</summary>
    public bool UsesHudDigits => UsesHudLook || GetBool("typography.hudDigits");

    public string BuildLink(string friendCodeDigits)
    {
        var patch = Patch();
        // These two travel as plain query values.
        var tag = patch["identity"]?["tag"]?.GetValue<string>() ?? string.Empty;
        var poll = (int)GetNumber("data.pollSeconds");
        patch.Remove("identity");
        patch.Remove("data");
        // "race hud" is Wheel Wizard's own VR change: the page counts, and the script draws the +N counting down.
        var raceHudChange = GetString("animations.vr") == "race hud";
        if (raceHudChange && patch["animations"] is JsonObject animationsPatch)
            animationsPatch["vr"] = "count";
        // MMR: the page reads the player from Wheel Wizard (the RR Lounge stats it already keeps, shaped like an
        // rwfc.net player) instead of rwfc.net, so every look option works the same for MMR.
        if (ShowsMmr)
            patch["data"] = new JsonObject
            {
                ["leaderboardUrl"] = $"{MmrDataUrl}/player/{{friendCode}}",
                ["groupsUrl"] = $"{MmrDataUrl}/rooms",
            };

        // The page doesn't know Wheel Wizard's own colours; they are added to it as CSS (see ColorCss), and only a
        // short marker travels in the link so the page reloads when one changes.
        var colors = string.Join("-", ColorKeys.Select(key => GetString($"typography.{key}").TrimStart('#')));
        if (patch["typography"] is JsonObject typography)
        {
            foreach (var key in ColorKeys)
                typography.Remove(key);
            typography.Remove("hudDigits");
            typography.Remove("rankOutlineColor");
            typography.Remove("rankOutlineWidth");
            typography.Remove("rankBadgeSize");
            typography.Remove("rankBadgeGap");
            if (typography.Count == 0)
                patch.Remove("typography");
        }

        var url = new StringBuilder($"{PageUrl}?view=overlay&fc={friendCodeDigits}");
        if (ShowsMmr)
            url.Append("&src=mmr");
        // MMR: the RR Lounge country flag before the name (Wheel Wizard adds the country to the link).
        if (ShowsMmr && GetBool("visibility.flag"))
            url.Append("&flag=on");
        if (colors != "8effc6-5ef0a5-ff6d7e-ffe88f")
            url.Append("&colors=").Append(Uri.EscapeDataString(colors));
        if (tag.Length > 0)
            url.Append("&tag=").Append(Uri.EscapeDataString(tag));
        // No card: only the text and numbers, like the race HUD (Wheel Wizard hides the card with CSS).
        if (patch["layout"] is JsonObject layoutPatch)
        {
            layoutPatch.Remove("card");
            layoutPatch.Remove("look");
            if (layoutPatch.Count == 0)
                patch.Remove("layout");
        }

        if (!ShowsCard)
            url.Append("&card=off");
        // The VR rank badge is Wheel Wizard's own too (see HudDigits).
        if (patch["visibility"] is JsonObject visibilityPatch)
        {
            visibilityPatch.Remove("rankBadge");
            visibilityPatch.Remove("rankOutline");
            visibilityPatch.Remove("flag");
            if (visibilityPatch.Count == 0)
                patch.Remove("visibility");
        }

        if (GetBool("visibility.rankBadge"))
            url.Append("&rank=on")
                .Append("&bs=")
                .Append((int)Math.Clamp(GetNumber("typography.rankBadgeSize"), 30, 300))
                .Append("&bg=")
                .Append((int)Math.Clamp(GetNumber("typography.rankBadgeGap"), 0, 100));
        if (UsesHudLook)
            url.Append("&text=hud");
        if (raceHudChange)
            url.Append("&vrhud=1");
        if (GetBool("visibility.rankBadge") && GetBool("visibility.rankOutline"))
            url.Append("&ro=")
                .Append(Uri.EscapeDataString(GetString("typography.rankOutlineColor").TrimStart('#')))
                .Append("&rw=")
                .Append((int)Math.Clamp(GetNumber("typography.rankOutlineWidth"), 1, 10));
        // Race HUD digits: Wheel Wizard draws them over the page's number (see HudDigits).
        if (UsesHudDigits)
            url.Append("&digits=hud");
        if (poll != 5 && poll is >= 1 and <= 600)
            url.Append("&poll=").Append(poll);
        if (patch.Count > 0)
        {
            var bytes = Encoding.UTF8.GetBytes(patch.ToJsonString());
            url.Append("&cfg=").Append(Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
        }

        return url.ToString();
    }

    private static readonly string[] ColorKeys = ["vrColor", "gainColor", "lossColor", "rankColor"];

    /// <summary>
    /// CSS for the overlay page with the VR number, gain, loss and rank colours, which the page itself has fixed. Only
    /// colours that are plain hex values make it in.
    /// </summary>
    public string ColorCss()
    {
        string Hex(string key, string fallback) =>
            GetString($"typography.{key}") is var value && System.Text.RegularExpressions.Regex.IsMatch(value, "^#[0-9a-fA-F]{6}$")
                ? value
                : fallback;

        var vr = Hex("vrColor", "#8effc6");
        var gain = Hex("gainColor", "#5ef0a5");
        var loss = Hex("lossColor", "#ff6d7e");
        var rank = Hex("rankColor", "#ffe88f");
        return $".vr-value{{color:{vr};--change-color:{gain};--change-glow:{gain}}}"
            + $".vr-value.change-negative{{--change-color:{loss};--change-glow:{loss}}}"
            + $".delta.positive{{color:{gain};background:{gain}30}}.delta.negative{{color:{loss};background:{loss}30}}"
            + $".positive{{color:{gain}!important}}.negative{{color:{loss}!important}}.rank-chip{{color:{rank}}}";
    }

    public int CardHeight()
    {
        var context = new[] { "room", "track", "sessionDelta", "dailyDelta" }.Count(k => GetBool($"visibility.{k}"));
        var compact = GetBool("layout.compact");
        return context > 2 ? (compact ? 186 : 198)
            : context > 0 ? (compact ? 160 : 176)
            : (compact ? 118 : 150);
    }
}
