using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using WheelWizard.Shared.Services;

namespace WheelWizard.RrLounge;

/// <summary>One line of a player's mogi history, as the site lists it (newest first).</summary>
public sealed record RrLoungeMogi(
    int Number,
    string Date,
    string Tier,
    string Format,
    string Races,
    string Place,
    string Score,
    string Result,
    string Change,
    int? Mmr
)
{
    public bool IsWin => Result.Equals("Win", StringComparison.OrdinalIgnoreCase);
    public bool IsLoss => Result.Equals("Loss", StringComparison.OrdinalIgnoreCase);

    /// <summary>An MMR fix by the staff rather than a mogi.</summary>
    public bool IsAdjustment => Format.Equals("Adjustment", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A player's page on rr-lounge.com: who they are, where they rank, their numbers and their mogis.</summary>
public sealed class RrLoungeProfile
{
    public string Name { get; init; } = string.Empty;
    public string? CountryCode { get; init; }
    public string Season { get; init; } = string.Empty;
    public string Division { get; init; } = string.Empty;

    /// <summary>The division's badge on the site, such as "/icons/new_diamond.png", or null.</summary>
    public string? DivisionIcon { get; init; }

    /// <summary>The colour the site writes the division in, such as "#b0f442", or null.</summary>
    public string? DivisionColor { get; init; }

    public int? Rank { get; init; }
    public int? Players { get; init; }
    public string? Percentile { get; init; }
    public int? Peak { get; init; }
    public int? Lowest { get; init; }

    /// <summary>The two stat tables, in the site's order: Wins, Losses, Win %, Avg Score, Mogis, Streak, W - L (10)…</summary>
    public List<(string Label, string Value)> Stats { get; init; } = [];

    public List<RrLoungeMogi> History { get; init; } = [];

    public int? Mmr => History.FirstOrDefault(mogi => mogi.Mmr is not null)?.Mmr;

    public string? Stat(string label) => Stats.FirstOrDefault(stat => stat.Label.Equals(label, StringComparison.OrdinalIgnoreCase)).Value;
}

public static partial class RrLoungeProfiles
{
    [GeneratedRegex("""<table[\s\S]*?</table>""")]
    private static partial Regex TableRegex();

    [GeneratedRegex("""<tr[^>]*>([\s\S]*?)</tr>""")]
    private static partial Regex RowRegex();

    [GeneratedRegex("""<td[^>]*>([\s\S]*?)</td>""")]
    private static partial Regex CellRegex();

    [GeneratedRegex("""<[^>]+>""")]
    private static partial Regex TagRegex();

    [GeneratedRegex("""\s+""")]
    private static partial Regex SpaceRegex();

    [GeneratedRegex("""<img src="/icons/[^"]*" alt="([^"]+)"[^>]*>\s*<p[^>]*>\s*DIVISION""")]
    private static partial Regex DivisionRegex();

    [GeneratedRegex("""<img src="(/icons/[a-z0-9_.-]+)" alt="[^"]+"[^>]*>\s*<p style="color:(#[0-9a-fA-F]{3,8})">\s*DIVISION""")]
    private static partial Regex DivisionLookRegex();

    [GeneratedRegex("""<div[^>]*text-3xl[^>]*>([\s\S]*?)</div>""")]
    private static partial Regex NameRegex();

    [GeneratedRegex("alt=\"([A-Za-z]{2})\"")]
    private static partial Regex CountryRegex();

    [GeneratedRegex("""<p class="text-base[^"]*">([\s\S]*?)</p>""")]
    private static partial Regex SeasonRegex();

    [GeneratedRegex("""Rank\s*:\s*(\d+)\s*/\s*(\d+)""")]
    private static partial Regex RankRegex();

    [GeneratedRegex("""Percentile\s*:\s*(\d+\w*)""")]
    private static partial Regex PercentileRegex();

    [GeneratedRegex("""Peak\s*:\s*(-?\d+)""")]
    private static partial Regex PeakRegex();

    [GeneratedRegex("""Lowest\s*:\s*(-?\d+)""")]
    private static partial Regex LowestRegex();

    /// <summary>Plain text of a piece of the page: no tags, no React markers, entities decoded, spaces collapsed.</summary>
    public static string Plain(string html) =>
        SpaceRegex().Replace(WebUtility.HtmlDecode(TagRegex().Replace(html.Replace("<!-- -->", ""), " ")), " ").Trim();

    private static List<List<string>> Rows(string table) =>
        RowRegex()
            .Matches(table)
            .Select(row => CellRegex().Matches(row.Groups[1].Value).Select(cell => Plain(cell.Groups[1].Value)).ToList())
            .Where(cells => cells.Count > 0)
            .ToList();

    private static int? Int(string? text) =>
        int.TryParse(text?.Replace(",", ""), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>Reads a player's page; null when it is not a player page (no such player, or the site changed).</summary>
    public static RrLoungeProfile? Parse(string? html)
    {
        if (string.IsNullOrEmpty(html))
            return null;
        var body = html.IndexOf("<body", StringComparison.Ordinal) is var start and >= 0 ? html[start..] : html;

        var stats = new List<(string, string)>();
        var history = new List<RrLoungeMogi>();
        foreach (Match table in TableRegex().Matches(body))
        {
            var rows = Rows(table.Value);
            if (rows.Count == 0)
                continue;
            var header = rows[0];
            if (header.Count > 1 && header[0] == "#" && header[1] == "Date")
            {
                foreach (var row in rows.Skip(1).Where(row => row.Count >= 10))
                    history.Add(new(Int(row[0]) ?? 0, row[1], row[2], row[3], row[4], row[5], row[6], row[7], row[8], Int(row[9])));
            }
            else if (rows.Count >= 2 && rows[1].Count == header.Count)
                stats.AddRange(header.Zip(rows[1]));
        }

        var name = NameRegex().Match(body);
        if (!name.Success && stats.Count == 0)
            return null;

        var text = Plain(body[..(body.IndexOf("<table", StringComparison.Ordinal) is var end and >= 0 ? end : body.Length)]);
        var rank = RankRegex().Match(text);
        var look = DivisionLookRegex().Match(body);
        return new RrLoungeProfile
        {
            Name = name.Success ? Plain(name.Groups[1].Value) : string.Empty,
            CountryCode =
                name.Success && CountryRegex().Match(name.Groups[1].Value) is { Success: true } country ? country.Groups[1].Value : null,
            Season = SeasonRegex().Match(body) is { Success: true } season ? Plain(season.Groups[1].Value) : string.Empty,
            Division = DivisionRegex().Match(body) is { Success: true } division
                ? WebUtility.HtmlDecode(division.Groups[1].Value)
                : string.Empty,
            DivisionIcon = look.Success ? look.Groups[1].Value : null,
            DivisionColor = look.Success ? look.Groups[2].Value : null,
            Rank = rank.Success ? Int(rank.Groups[1].Value) : null,
            Players = rank.Success ? Int(rank.Groups[2].Value) : null,
            Percentile = PercentileRegex().Match(text) is { Success: true } percentile ? percentile.Groups[1].Value : null,
            Peak = Int(PeakRegex().Match(text).Groups[1].Value),
            Lowest = Int(LowestRegex().Match(text).Groups[1].Value),
            Stats = stats,
            History = history,
        };
    }

    public static async Task<RrLoungeProfile?> LoadAsync(IApiCaller<IRrLoungeApi> api, string name, RrLoungeLadder ladder, string? season)
    {
        var slug = RrLoungeData.PlayerSlug(name);
        var category = RrLoungeData.Query(ladder);
        var page = await api.CallApiAsync(lounge => lounge.GetPlayerPageAsync(slug, category, season));
        return page.IsSuccess ? Parse(page.Value) : null;
    }

    /// <summary>"Diamond" from "DIAMOND", the way the leaderboard writes rank names.</summary>
    public static string Titled(string text) =>
        text.Length == 0 ? text : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant());
}
