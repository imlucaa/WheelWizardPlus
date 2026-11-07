using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using NSubstitute;
using WheelWizard.LoungeStats;
using WheelWizard.RrLounge;
using WheelWizard.Settings;
using WheelWizard.Settings.Types;
using WheelWizard.Shared;
using WheelWizard.Shared.Services;
using WheelWizard.Test.Features.RrLounge;

namespace WheelWizard.Test.Features.LoungeStats;

public class LoungeStatsTests
{
    #region Fakes

    /// <summary>A clock the test moves by hand. Its timers never fire: the test calls PollAsync itself.</summary>
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 5, 8, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new Never();

        private sealed class Never : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() { }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>RR Lounge as the test wants it: one ladder of players and one player page, and a switch to take it offline.</summary>
    private sealed class Site : IRrLoungeApi
    {
        public List<RrLoungePlayer> Players = [];
        public string? PlayerPage;
        public bool Down;
        public bool PageDown;
        public int BoardReads;
        public int PageReads;
        public string? LastSlug;

        public Task<RrLoungeBoard> GetLeaderboardAsync(string category, string? season)
        {
            BoardReads++;
            if (Down)
                throw new HttpRequestException("offline");
            // Only the RR ladder has players here.
            var players = category == "rr" ? Players : [];
            return Task.FromResult(
                new RrLoungeBoard
                {
                    Category = category,
                    PlayerCount = players.Count,
                    Players = players
                        .Select(p => new RrLoungePlayer
                        {
                            Rank = p.Rank,
                            Id = p.Id,
                            Name = p.Name,
                            Mmr = p.Mmr,
                        })
                        .ToList(),
                }
            );
        }

        public Task<string> GetLeaderboardPageAsync(string category, string? season) => throw new NotSupportedException();

        public Task<string> GetPlayerPageAsync(string slug, string category, string? season)
        {
            PageReads++;
            LastSlug = slug;
            if (Down || PageDown || PlayerPage is null)
                throw new TaskCanceledException("timed out");
            return Task.FromResult(PlayerPage);
        }
    }

    private sealed class Caller(IRrLoungeApi site) : IApiCaller<IRrLoungeApi>
    {
        public async Task<OperationResult<TResult>> CallApiAsync<TResult>(Expression<Func<IRrLoungeApi, Task<TResult>>> apiCall)
        {
            try
            {
                return await apiCall.Compile()(site);
            }
            catch (Exception exception)
            {
                return OperationError.Fail(exception);
            }
        }
    }

    // A player page like the site's, for the numbers the stats need.
    private static string Page(string name, string division, string color, int wins, int losses, string country = "us") =>
        $@"<html><body><div><img src=""/icons/new_{division.ToLowerInvariant()}.png"" alt=""{division}"" width=""100"" height=""100""/><p style=""color:{color}"">DIVISION: <!-- -->{division.ToUpperInvariant()}</p></div>
<div class=""flex items-center gap-2 text-3xl""><img src=""https://flagcdn.com/16x12/{country}.png"" width=""16"" height=""12"" alt=""{country.ToUpperInvariant()}"" class=""inline-block""/>{name}</div>
<table><tbody><tr><td>Wins</td><td>Losses</td><td>Win %</td></tr><tr><td><span>{wins}</span></td><td><span>{losses}</span></td><td>70.0</td></tr></tbody></table></body></html>";

    private static ISettingsManager Settings(Dictionary<string, string>? store = null)
    {
        store ??= [];
        var settings = Substitute.For<ISettingsManager>();
        var config = new WhWzSetting<string>("LoungeStatsConfig", "");
        var cache = new WhWzSetting<string>("LoungeStatsCache", "");
        settings.LOUNGE_STATS_CONFIG.Returns(config);
        settings.LOUNGE_STATS_CACHE.Returns(cache);
        settings.Get(Arg.Any<Setting<string>>()).Returns(call => store.GetValueOrDefault(((Setting)call[0]).Name, ""));
        settings
            .Set(Arg.Any<Setting<string>>(), Arg.Any<string>())
            .Returns(call =>
            {
                store[((Setting)call[0]).Name] = (string)call[1];
                return true;
            });
        return settings;
    }

    private static (LoungeStatsService Service, LoungeStatsConfig Config, Site Site, Clock Clock, Dictionary<string, string> Store) Make(
        Action<Site>? site = null,
        Dictionary<string, string>? store = null
    )
    {
        store ??= [];
        var fake = new Site
        {
            Players =
            [
                new()
                {
                    Rank = 1,
                    Id = "a1",
                    Name = "intz",
                    Mmr = 15192,
                },
                new()
                {
                    Rank = 2,
                    Id = "b2",
                    Name = "Lumis",
                    Mmr = 4250,
                },
            ],
            PlayerPage = Page("Lumis", "Amethyst", "#b145e3", 120, 45),
        };
        site?.Invoke(fake);
        var settings = Settings(store);
        var config = new LoungeStatsConfig(settings);
        var clock = new Clock();
        var service = new LoungeStatsService(new Caller(fake), config, settings, clock);
        return (service, config, fake, clock, store);
    }

    #endregion

    [Fact]
    public void ThePlayerPage_GivesTheDivisionBadge_AndItsColour()
    {
        var profile = RrLoungeProfiles.Parse(RrLoungeProfileTests.Page)!;

        Assert.Equal("/icons/new_diamond.png", profile.DivisionIcon);
        Assert.Equal("#b0f442", profile.DivisionColor);
    }

    [Fact]
    public async Task NothingIsRead_WhileNothingShowsTheStats()
    {
        var (service, config, site, _, _) = Make();
        config.SetPlayer("Lumis", "");

        await service.PollAsync();

        Assert.Equal(0, site.BoardReads);
        Assert.Null(service.Current);
    }

    [Fact]
    public async Task TheFirstRead_FindsThePlayerByName_AndReadsTheirPage()
    {
        var (service, config, site, _, store) = Make();
        config.SetPlayer("lumis", "");
        using var lease = service.Use(); // Starts the first read; the fake site answers at once.

        var stats = service.Current!;
        Assert.Equal("Lumis", stats.Name);
        Assert.Equal("b2", stats.Id);
        Assert.Equal(4250, stats.Mmr);
        Assert.Equal(2, stats.Place);
        Assert.Equal(2, stats.Players);
        Assert.Equal("Amethyst", stats.Division);
        Assert.Equal("#b145e3", stats.DivisionColor);
        Assert.Equal("https://rr-lounge.com/icons/new_amethyst.png", stats.BadgeUrl);
        Assert.Equal("US", stats.CountryCode);
        Assert.Equal("https://flagcdn.com/w80/us.png", stats.FlagUrl);
        Assert.Equal(120, stats.Wins);
        Assert.Equal(45, stats.Losses);
        Assert.Equal("lumis", site.LastSlug);
        Assert.Null(service.Problem);
        Assert.False(service.IsStale);

        // The site's id and exact name are kept, and the numbers are saved for next time.
        Assert.Equal("b2", config.PlayerId);
        Assert.Equal("Lumis", config.PlayerName);
        Assert.Equal(
            stats with
            {
                FetchedAt = default,
            },
            LoungeStatsSnapshot.FromJson(store["LoungeStatsCache"])! with
            {
                FetchedAt = default,
            }
        );
    }

    [Fact]
    public async Task AReadWithTheSameMmr_DoesNotReadThePlayerPageAgain()
    {
        var (service, config, site, clock, _) = Make();
        config.SetPlayer("Lumis", "");
        using var lease = service.Use(); // Starts the first read; the fake site answers at once.
        var changes = 0;
        service.Changed += () => changes++;

        clock.Now += TimeSpan.FromMinutes(1);
        await service.PollAsync();

        Assert.Equal(2, site.BoardReads);
        Assert.Equal(1, site.PageReads);
        Assert.Equal(0, changes);
        Assert.Null(service.LastChange);
    }

    [Fact]
    public async Task ReadsWaitForTheRefreshTime()
    {
        var (service, config, site, clock, _) = Make();
        config.SetPlayer("Lumis", "");
        config.Set("data.refreshSeconds", JsonValue.Create(120));
        using var lease = service.Use(); // Starts the first read; the fake site answers at once.

        clock.Now += TimeSpan.FromSeconds(119);
        await service.PollAsync();
        Assert.Equal(1, site.BoardReads);

        clock.Now += TimeSpan.FromSeconds(2);
        await service.PollAsync();
        Assert.Equal(2, site.BoardReads);
    }

    [Fact]
    public async Task AWin_MovesTheMmrAndWins_AndCountsAsAChange()
    {
        var (service, config, site, clock, _) = Make();
        config.SetPlayer("Lumis", "");
        using var lease = service.Use(); // Starts the first read; the fake site answers at once.

        site.Players[1].Mmr = 4275;
        site.PlayerPage = Page("Lumis", "Amethyst", "#b145e3", 121, 45);
        clock.Now += TimeSpan.FromMinutes(1);
        await service.PollAsync();

        Assert.Equal(4275, service.Current!.Mmr);
        Assert.Equal(121, service.Current.Wins);
        Assert.Equal(45, service.Current.Losses);
        Assert.Equal(new LoungeStatsChange(25, WinsUp: true, LossesUp: false, RankDirection: 0, PlaceChanged: false), service.LastChange);
        Assert.Equal(1, service.ChangeNumber);
        Assert.Equal(2, site.PageReads);
    }

    [Fact]
    public async Task ALossIntoALowerDivision_IsADemotion()
    {
        var (service, config, site, clock, _) = Make();
        config.SetPlayer("Lumis", "");
        using var lease = service.Use(); // Starts the first read; the fake site answers at once.

        site.Players[1].Mmr = 4180;
        site.PlayerPage = Page("Lumis", "Platinum", "#3fabb8", 120, 46);
        clock.Now += TimeSpan.FromMinutes(1);
        await service.PollAsync();

        Assert.Equal("Platinum", service.Current!.Division);
        Assert.Equal(new LoungeStatsChange(-70, false, true, -1, false), service.LastChange);
    }

    [Fact]
    public void APromotion_IsARankUp()
    {
        var before = new LoungeStatsSnapshot
        {
            Id = "b2",
            Mmr = 4480,
            Division = "Platinum",
            Wins = 1,
            Losses = 0,
        };
        var after = before with { Mmr = 4510, Division = "Amethyst", Wins = 2 };

        Assert.Equal(new LoungeStatsChange(30, true, false, 1, false), LoungeStatsChange.Between(before, after));
        Assert.Null(LoungeStatsChange.Between(before, before));
        Assert.Null(LoungeStatsChange.Between(null, after));
        Assert.Null(LoungeStatsChange.Between(before with { Id = "someone else" }, after));
    }

    [Fact]
    public async Task WhenTheSiteIsDown_TheLastStatsStay_AndTheNextTryWaitsLonger()
    {
        var (service, config, site, clock, _) = Make();
        config.SetPlayer("Lumis", "");
        using var lease = service.Use(); // Starts the first read; the fake site answers at once.
        var last = service.Current;

        site.Down = true;
        clock.Now += TimeSpan.FromMinutes(1);
        await service.PollAsync();

        Assert.Same(last, service.Current);
        Assert.True(service.IsStale);
        Assert.Contains("could not be reached", service.Problem);

        // One failure doubles the wait: nothing at 1 minute, a try at 2.
        clock.Now += TimeSpan.FromSeconds(61);
        await service.PollAsync();
        Assert.Equal(2, site.BoardReads);
        clock.Now += TimeSpan.FromSeconds(60);
        await service.PollAsync();
        Assert.Equal(3, site.BoardReads);

        // Back up: fresh numbers, no problem.
        site.Down = false;
        clock.Now += TimeSpan.FromMinutes(5);
        await service.PollAsync();
        Assert.False(service.IsStale);
        Assert.Null(service.Problem);
    }

    [Fact]
    public void TheWaitAfterFailures_DoublesUpToTenMinutes()
    {
        var minute = TimeSpan.FromMinutes(1);
        Assert.Equal(TimeSpan.FromMinutes(2), LoungeStatsService.Backoff(minute, 1));
        Assert.Equal(TimeSpan.FromMinutes(8), LoungeStatsService.Backoff(minute, 3));
        Assert.Equal(TimeSpan.FromMinutes(10), LoungeStatsService.Backoff(minute, 7));
    }

    [Fact]
    public async Task APlayerPageThatTimesOut_KeepsTheLastWinsAndLosses_AndIsAskedForAgain()
    {
        var (service, config, site, clock, _) = Make();
        config.SetPlayer("Lumis", "");
        using var lease = service.Use(); // Starts the first read; the fake site answers at once.

        site.Players[1].Mmr = 4300;
        site.PageDown = true;
        clock.Now += TimeSpan.FromMinutes(1);
        await service.PollAsync();

        Assert.Equal(4300, service.Current!.Mmr);
        Assert.Equal(120, service.Current.Wins);
        Assert.Equal("Amethyst", service.Current.Division);
        Assert.NotNull(service.Problem);

        // The page is asked for again on the next read even though the MMR did not move since.
        site.PageDown = false;
        site.PlayerPage = Page("Lumis", "Amethyst", "#b145e3", 121, 45);
        clock.Now += TimeSpan.FromMinutes(1);
        await service.PollAsync();
        Assert.Equal(121, service.Current.Wins);
        Assert.Null(service.Problem);
    }

    [Fact]
    public async Task APageForSomeoneElse_IsNotUsed()
    {
        // "Lumis!" and "lumis" share the link /players/lumis, so the page's name must match.
        var (service, config, _, _, _) = Make(site => site.PlayerPage = Page("lumis!", "Master", "#ffffff", 900, 1));
        config.SetPlayer("Lumis", "");
        using var lease = service.Use(); // Starts the first read; the fake site answers at once.

        Assert.Null(service.Current!.Wins);
        Assert.Null(service.Current.Division);
    }

    [Fact]
    public async Task APlayerNotOnTheLadder_SaysSo()
    {
        var (service, config, _, _, _) = Make();
        config.SetPlayer("Nobody", "");
        using var lease = service.Use(); // Starts the first read; the fake site answers at once.

        Assert.Null(service.Current);
        Assert.Equal("Nobody is not on the RR leaderboard this season.", service.Problem);
    }

    [Fact]
    public async Task ANameChangeOnTheSite_IsFollowedByTheId()
    {
        var (service, config, site, clock, _) = Make();
        config.SetPlayer("Lumis", "");
        using var lease = service.Use(); // Starts the first read; the fake site answers at once.

        site.Players[1].Name = "Lumis2";
        site.PlayerPage = Page("Lumis2", "Amethyst", "#b145e3", 120, 45, country: "ca");
        clock.Now += TimeSpan.FromMinutes(1);
        await service.PollAsync();

        Assert.Equal("Lumis2", service.Current!.Name);
        Assert.Equal("CA", service.Current.CountryCode);
        Assert.Equal("Lumis2", config.PlayerName);
    }

    [Fact]
    public async Task PickingAnotherPlayer_DropsTheOldStatsAtOnce()
    {
        var (service, config, _, _, _) = Make();
        config.SetPlayer("Lumis", "");
        using var lease = service.Use(); // Starts the first read; the fake site answers at once.
        Assert.NotNull(service.Current);

        config.Set("player.ladder", JsonValue.Create("ct"));

        Assert.Null(service.Current);
        Assert.Equal("Lumis is not on the CT leaderboard this season.", service.Problem);
    }

    [Fact]
    public async Task SavedStats_ShowRightAway_AfterARestart()
    {
        var store = new Dictionary<string, string>();
        var (first, config, _, _, _) = Make(store: store);
        config.SetPlayer("Lumis", "");
        using (first.Use()) { }

        var (second, _, site, _, _) = Make(store: store);

        Assert.Equal(4250, second.Current!.Mmr);
        Assert.Equal(120, second.Current.Wins);
        Assert.True(second.IsStale);
        Assert.Equal(0, site.BoardReads);
    }

    [Fact]
    public void TheLook_IsSaved_AndReadBackByANewConfig()
    {
        var store = new Dictionary<string, string>();
        var settings = Settings(store);
        var config = new LoungeStatsConfig(settings);
        config.Set("show.wins", JsonValue.Create(false));
        config.Set("layout.style", JsonValue.Create("hud"));
        config.SetPlayer("Lumis", "b2");

        // Only what differs from the defaults is written.
        var saved = JsonNode.Parse(store["LoungeStatsConfig"])!.AsObject();
        Assert.Equal(["layout", "player", "show"], saved.Select(pair => pair.Key).Order());
        Assert.Single(saved["show"]!.AsObject());

        var reloaded = new LoungeStatsConfig(settings);
        Assert.False(reloaded.GetBool("show.wins"));
        Assert.True(reloaded.GetBool("show.losses"));
        Assert.Equal("hud", reloaded.GetString("layout.style"));
        Assert.Equal("b2", reloaded.PlayerId);

        // Reset puts the look back but keeps whose stats they are.
        reloaded.Reset();
        Assert.True(reloaded.GetBool("show.wins"));
        Assert.Equal("Lumis", reloaded.PlayerName);
    }

    [Fact]
    public void TheRefreshTime_StaysBetweenThirtySecondsAndTenMinutes()
    {
        var config = new LoungeStatsConfig(Settings());
        config.Set("data.refreshSeconds", JsonValue.Create(5));
        Assert.Equal(TimeSpan.FromSeconds(30), config.RefreshInterval);
        config.Set("data.refreshSeconds", JsonValue.Create(9999));
        Assert.Equal(TimeSpan.FromMinutes(10), config.RefreshInterval);
    }

    [Fact]
    public async Task TheObsState_FollowsEveryToggle_AndOnlyLetsPlainColoursThrough()
    {
        var (service, config, _, _, _) = Make();
        config.SetPlayer("Lumis", "");
        using var lease = service.Use(); // Starts the first read; the fake site answers at once.
        config.Set("show.flag", JsonValue.Create(false));
        config.Set("show.labels", JsonValue.Create(false));
        config.Set("colors.text", JsonValue.Create("red;background:url(x)"));
        config.Set("colors.divisionColor", JsonValue.Create(false));
        config.Set("colors.rank", JsonValue.Create("#abc"));

        using var state = JsonDocument.Parse(LoungeStatsPage.State(service, config));
        var root = state.RootElement;
        var look = root.GetProperty("look");

        Assert.True(root.GetProperty("visible").GetBoolean());
        Assert.False(look.GetProperty("showFlag").GetBoolean());
        Assert.False(look.GetProperty("showLabels").GetBoolean());
        Assert.True(look.GetProperty("showWins").GetBoolean());
        Assert.Equal("#FFFFFF", look.GetProperty("text").GetString());
        Assert.Equal("#AABBCC", look.GetProperty("rank").GetString());
        Assert.Equal(4250, root.GetProperty("stats").GetProperty("mmr").GetInt32());
        Assert.Equal("AMETHYST", root.GetProperty("stats").GetProperty("division").GetString());
        Assert.StartsWith(LoungeStatsPage.BadgePath, root.GetProperty("stats").GetProperty("badge").GetString());

        var before = root.GetProperty("version").GetString();
        config.Set("show.component", JsonValue.Create(false));
        using var hidden = JsonDocument.Parse(LoungeStatsPage.State(service, config));
        Assert.False(hidden.RootElement.GetProperty("visible").GetBoolean());
        Assert.NotEqual(before, hidden.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public void TheObsState_WithNoStats_IsHidden()
    {
        var (service, config, _, _, _) = Make();

        using var state = JsonDocument.Parse(LoungeStatsPage.State(service, config));

        Assert.False(state.RootElement.GetProperty("visible").GetBoolean());
        Assert.Equal(JsonValueKind.Null, state.RootElement.GetProperty("stats").ValueKind);
    }
}
