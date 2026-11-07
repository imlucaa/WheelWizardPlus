using System.Text.Json;
using WheelWizard.RrLounge;

namespace WheelWizard.LoungeStats;

/// <summary>
/// One player's numbers on RR Lounge, as last read: the leaderboard gives the name, id, place and MMR, the player's page
/// the country, division and wins and losses. Kept on disk, so the overlay has numbers to show right after a restart.
/// </summary>
public sealed record LoungeStatsSnapshot
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Ladder { get; init; } = "rr";
    public string? CountryCode { get; init; }

    /// <summary>The division ("Master", "Diamond"…), the competitive rank.</summary>
    public string? Division { get; init; }

    public string? DivisionColor { get; init; }

    /// <summary>The division's badge on the site, such as "/icons/new_master.png".</summary>
    public string? DivisionIcon { get; init; }

    /// <summary>The place on the leaderboard (1 is the top) and how many players it has.</summary>
    public int? Place { get; init; }

    public int? Players { get; init; }
    public int Mmr { get; init; }
    public int? Wins { get; init; }
    public int? Losses { get; init; }

    /// <summary>When the leaderboard was last read for these numbers.</summary>
    public DateTimeOffset FetchedAt { get; init; }

    /// <summary>When the player's page was last read (country, division, wins and losses).</summary>
    public DateTimeOffset ProfileAt { get; init; }

    public bool HasProfile => ProfileAt != default;

    /// <summary>The badge's full address, or null.</summary>
    public string? BadgeUrl => RrLoungeData.IconUrl(new RrLoungeTier { Icon = DivisionIcon });

    /// <summary>A bigger flag than the leaderboard's 16×12 one, sharp at overlay sizes, or null without a country.</summary>
    public string? FlagUrl =>
        CountryCode is { Length: 2 } code && code.All(char.IsAsciiLetter) ? $"https://flagcdn.com/w80/{code.ToLowerInvariant()}.png" : null;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>The saved numbers, or null when there are none or they cannot be read.</summary>
    public static LoungeStatsSnapshot? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<LoungeStatsSnapshot>(json, Json) is { Name.Length: > 0 } snapshot ? snapshot : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The numbers from a fresh read: the leaderboard's line for the player, the player's page when it was read this time,
    /// and the last snapshot for whatever this read did not cover. A page for someone else (two names can share a link)
    /// is not used.
    /// </summary>
    public static LoungeStatsSnapshot From(
        RrLoungePlayer player,
        int? players,
        string ladder,
        RrLoungeProfile? profile,
        LoungeStatsSnapshot? previous,
        DateTimeOffset now
    )
    {
        var page = profile is not null && profile.Name.Equals(player.Name, StringComparison.OrdinalIgnoreCase) ? profile : null;
        var old = previous is not null && previous.Id == player.Id && previous.Ladder == ladder ? previous : null;
        return new LoungeStatsSnapshot
        {
            Id = player.Id,
            Name = player.Name,
            Ladder = ladder,
            CountryCode = page?.CountryCode ?? player.CountryCode ?? old?.CountryCode,
            Division = page is { Division.Length: > 0 } ? RrLoungeProfiles.Titled(page.Division) : old?.Division,
            DivisionColor = page is not null ? page.DivisionColor ?? old?.DivisionColor : old?.DivisionColor,
            DivisionIcon = page is not null ? page.DivisionIcon ?? old?.DivisionIcon : old?.DivisionIcon,
            Place = player.Rank > 0 ? player.Rank : old?.Place,
            Players = players ?? old?.Players,
            Mmr = player.Mmr,
            Wins = page is not null ? Count(page.Stat("Wins")) ?? old?.Wins : old?.Wins,
            Losses = page is not null ? Count(page.Stat("Losses")) ?? old?.Losses : old?.Losses,
            FetchedAt = now,
            ProfileAt = page is not null ? now : old?.ProfileAt ?? default,
        };
    }

    private static int? Count(string? text) =>
        int.TryParse(
            text?.Replace(",", ""),
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value
        )
            ? value
            : null;
}

/// <summary>What moved between two reads of the same player, for the overlay's change animations.</summary>
public sealed record LoungeStatsChange(int MmrDelta, bool WinsUp, bool LossesUp, int RankDirection, bool PlaceChanged)
{
    public bool IsEmpty => MmrDelta == 0 && !WinsUp && !LossesUp && RankDirection == 0 && !PlaceChanged;

    /// <summary>
    /// The change from <paramref name="before"/> to <paramref name="after"/>, or null for a first read or another
    /// player. A new division counts as a promotion when the MMR went up and a demotion when it went down.
    /// </summary>
    public static LoungeStatsChange? Between(LoungeStatsSnapshot? before, LoungeStatsSnapshot after)
    {
        if (before is null || before.Id != after.Id || before.Ladder != after.Ladder)
            return null;

        var delta = after.Mmr - before.Mmr;
        var rank =
            before.Division is { } oldDivision
            && after.Division is { } newDivision
            && !oldDivision.Equals(newDivision, StringComparison.OrdinalIgnoreCase)
                ? (delta >= 0 ? 1 : -1)
                : 0;
        var change = new LoungeStatsChange(
            delta,
            after.Wins > before.Wins,
            after.Losses > before.Losses,
            rank,
            after.Place != before.Place && before.Place is not null
        );
        return change.IsEmpty ? null : change;
    }
}
