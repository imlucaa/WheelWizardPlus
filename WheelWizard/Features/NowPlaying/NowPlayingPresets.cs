using System.Text.Json.Nodes;

namespace WheelWizard.NowPlaying;

/// <summary>A ready-made look for the Now playing card: its values on top of the defaults.</summary>
public sealed record NowPlayingPreset(string Name, string Description, IReadOnlyList<KeyValuePair<string, JsonNode>> Values)
{
    public static NowPlayingPreset Of(string name, string description, params (string Path, object Value)[] values) =>
        new(
            name,
            description,
            values
                .Select(pair => new KeyValuePair<string, JsonNode>(
                    pair.Path,
                    pair.Value switch
                    {
                        bool flag => JsonValue.Create(flag),
                        int number => JsonValue.Create(number),
                        double number => JsonValue.Create(number),
                        string text => JsonValue.Create(text),
                        _ => throw new ArgumentException($"Preset value for {pair.Path} must be a bool, number or text."),
                    }
                ))
                .ToList()
        );
}

/// <summary>The looks the Now playing tab offers with one click.</summary>
public static class NowPlayingPresets
{
    public static readonly IReadOnlyList<NowPlayingPreset> All =
    [
        NowPlayingPreset.Of("Classic", "Cover and two cards in the cover's color."),
        NowPlayingPreset.Of(
            "Vinyl",
            "A round cover that turns like a record, above the cards.",
            ("layout.style", "vertical"),
            ("layout.coverShape", "circle"),
            ("layout.coverSize", 130),
            ("layout.width", 300),
            ("layout.align", "center"),
            ("effects.spin", true)
        ),
        NowPlayingPreset.Of(
            "Strip",
            "One thin line with the song and a progress line.",
            ("layout.style", "strip"),
            ("layout.width", 440),
            ("layout.radius", 14),
            ("show.time", false)
        ),
        NowPlayingPreset.Of(
            "Text only",
            "Just the words, with a shadow so they read on any scene.",
            ("layout.style", "text"),
            ("show.cover", false),
            ("show.bars", false),
            ("show.progress", false),
            ("effects.textShadow", true),
            ("layout.titleWeight", "black")
        ),
        NowPlayingPreset.Of(
            "Big cover",
            "A large cover with the song under it.",
            ("layout.style", "vertical"),
            ("layout.coverSize", 200),
            ("layout.width", 260),
            ("layout.radius", 16),
            ("layout.align", "center"),
            ("show.bars", false)
        ),
        NowPlayingPreset.Of(
            "Compact",
            "The cover and one card, nothing else.",
            ("layout.style", "compact"),
            ("show.bars", false),
            ("layout.width", 320)
        ),
        NowPlayingPreset.Of(
            "Neon",
            "Dark cards with bright lines and a glowing border.",
            ("colors.source", "custom"),
            ("colors.custom", "#1B1B3A"),
            ("colors.text", "#F8F8FF"),
            ("colors.progress", "#00F5D4"),
            ("colors.bars", "#FF3EA5"),
            ("effects.borderWidth", 2),
            ("effects.borderColor", "#00F5D4"),
            ("effects.gradient", true),
            ("effects.cardShadow", true),
            ("layout.radius", 14),
            ("layout.fontFamily", "Trebuchet MS")
        ),
        NowPlayingPreset.Of(
            "Glass",
            "See-through cards with a soft edge.",
            ("colors.cardOpacity", 0.38),
            ("layout.radius", 18),
            ("effects.borderWidth", 1),
            ("effects.glow", false),
            ("effects.textShadow", true)
        ),
        NowPlayingPreset.Of(
            "Terminal",
            "Green on dark, square corners and capitals.",
            ("colors.source", "custom"),
            ("colors.custom", "#1C1F1C"),
            ("colors.text", "#7CFC00"),
            ("colors.progress", "#7CFC00"),
            ("colors.bars", "#7CFC00"),
            ("effects.borderWidth", 2),
            ("effects.borderColor", "#7CFC00"),
            ("effects.glow", false),
            ("layout.radius", 0),
            ("layout.fontFamily", "Consolas"),
            ("layout.uppercase", true),
            ("layout.titleWeight", "black"),
            ("layout.barCount", 30)
        ),
    ];
}
