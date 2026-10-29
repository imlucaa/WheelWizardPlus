using System.Text.Json;
using System.Text.Json.Nodes;
using NSubstitute;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using WheelWizard.NowPlaying;
using WheelWizard.Settings;
using WheelWizard.Settings.Types;

namespace WheelWizard.Test.Features.NowPlayingTests;

public class NowPlayingTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Parse_ReadsTheSongTheMediaReaderPrints()
    {
        var cover = Convert.ToBase64String([1, 2, 3]);
        var line =
            $$"""{"app":"Spotify.exe","playing":true,"title":"Song","artist":"Artist","album":"Album","cover":"{{cover}}","position":12.5,"duration":200}""";

        var info = NowPlayingInfo.Parse(line);

        Assert.NotNull(info);
        Assert.Equal("Song", info.Title);
        Assert.Equal("Artist", info.Artist);
        Assert.Equal("Album", info.Album);
        Assert.Equal("Spotify", info.App);
        Assert.True(info.IsPlaying);
        Assert.Equal<byte[]>([1, 2, 3], info.Cover!);
        Assert.Equal(12.5, info.Position);
        Assert.Equal(200, info.Duration);
        Assert.True(info.HasLength);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"title":"  "}""")]
    public void Parse_GivesNothingForAnEmptyOrBrokenLine(string line) => Assert.Null(NowPlayingInfo.Parse(line));

    [Fact]
    public void Parse_KeepsTheSongWhenTheCoverAndTimesAreBroken()
    {
        var info = NowPlayingInfo.Parse("""{"title":"Song","cover":"%%%","position":"soon","duration":-5}""");

        Assert.NotNull(info);
        Assert.Null(info.Cover);
        Assert.False(info.IsPlaying);
        Assert.Equal(0, info.Position);
        Assert.Equal(0, info.Duration);
        Assert.False(info.HasLength);
    }

    [Theory]
    [InlineData("Spotify.exe", "Spotify")]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "Spotify")]
    [InlineData("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", "")]
    [InlineData("chrome.exe", "Chrome")]
    [InlineData("MSEdge", "Edge")]
    [InlineData("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App", "Apple Music")]
    [InlineData("Apple Music", "Apple Music")]
    [InlineData("iTunes.exe", "iTunes")]
    [InlineData("Amazon Music.exe", "Amazon Music")]
    [InlineData("Opera.exe", "Opera")]
    public void FriendlyApp_NamesThePlayer(string id, string expected) => Assert.Equal(expected, NowPlayingInfo.FriendlyApp(id));

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(5.9, "0:05")]
    [InlineData(125, "2:05")]
    [InlineData(3669, "1:01:09")]
    [InlineData(-3, "0:00")]
    public void FormatTime_ShowsMinutesAndSeconds(double seconds, string expected) =>
        Assert.Equal(expected, NowPlayingInfo.FormatTime(seconds));

    [Fact]
    public void Elapsed_MovesOnWhilePlaying_StaysStillWhenPaused_AndNeverPassesTheEnd()
    {
        var playing = new NowPlayingInfo("S", "A", "", "", true, null, 100, 130, Noon);
        var paused = playing with { IsPlaying = false };

        Assert.Equal(110, playing.ElapsedAt(Noon.AddSeconds(10)));
        Assert.Equal(130, playing.ElapsedAt(Noon.AddSeconds(500)));
        Assert.Equal(100, paused.ElapsedAt(Noon.AddSeconds(10)));
        Assert.Equal(100, playing.ElapsedAt(Noon.AddSeconds(-4)));
    }

    [Fact]
    public void State_HidesThePageWhenNothingPlaysAndThatOptionIsOn()
    {
        var paused = new NowPlayingInfo("Song", "Artist", "", "Spotify", false, null);
        var config = Config();

        Assert.False(Visible(NowPlayingPage.State(paused, config, Noon)));
        Assert.False(Visible(NowPlayingPage.State(null, config, Noon)));
        Assert.True(Visible(NowPlayingPage.State(paused with { IsPlaying = true }, config, Noon)));

        config.Set("behavior.hideWhenNotPlaying", JsonValue.Create(false));
        Assert.True(Visible(NowPlayingPage.State(paused, config, Noon)));
        Assert.False(Visible(NowPlayingPage.State(null, config, Noon)));
    }

    [Fact]
    public void State_FollowsTheLook_AndCarriesTheTime()
    {
        var info = new NowPlayingInfo("Song", "Artist", "", "Apple Music", true, [1, 2], 20, 180, Noon);
        var config = Config();
        config.Set("show.artist", JsonValue.Create(false));
        config.Set("show.cover", JsonValue.Create(false));
        config.Set("layout.style", JsonValue.Create("compact"));
        config.Set("colors.source", JsonValue.Create("custom"));
        config.Set("colors.custom", JsonValue.Create("#abc"));

        using var state = JsonDocument.Parse(NowPlayingPage.State(info, config, Noon.AddSeconds(5)));
        var root = state.RootElement;

        Assert.Equal("", root.GetProperty("artist").GetString());
        Assert.False(root.GetProperty("cover").GetBoolean());
        Assert.Equal("Apple Music", root.GetProperty("app").GetString());
        Assert.Equal("#AABBCC", root.GetProperty("color").GetString());
        Assert.Equal(25, root.GetProperty("position").GetDouble());
        Assert.Equal(180, root.GetProperty("duration").GetDouble());
        Assert.Equal("compact", root.GetProperty("look").GetProperty("style").GetString());
    }

    [Fact]
    public void Config_StartsWithTheDefaults_AndSavesOnlyWhatWasChanged()
    {
        var saved = "";
        var settings = Substitute.For<ISettingsManager>();
        settings.Set(Arg.Any<Setting<string>>(), Arg.Do<string>(value => saved = value)).Returns(true);
        var config = new NowPlayingConfig(settings);

        Assert.Equal("classic", config.GetString("layout.style"));
        Assert.Equal(360, config.GetNumber("layout.width"));
        Assert.True(config.GetBool("show.progress"));

        var changes = 0;
        config.Changed += () => changes++;
        config.Set("layout.scale", JsonValue.Create(1.5));
        config.Set("show.bars", JsonValue.Create(false));

        Assert.Equal(2, changes);
        using var document = JsonDocument.Parse(saved);
        Assert.Equal(1.5, document.RootElement.GetProperty("layout").GetProperty("scale").GetDouble());
        Assert.False(document.RootElement.GetProperty("show").GetProperty("bars").GetBoolean());
        Assert.False(document.RootElement.TryGetProperty("colors", out _));
        Assert.False(document.RootElement.GetProperty("layout").TryGetProperty("style", out _));

        config.Reset();
        Assert.Equal("", saved);
        Assert.Equal(1, config.GetNumber("layout.scale"));
    }

    [Fact]
    public void Config_LoadsWhatWasSaved_AndSkipsValuesOfTheWrongKind()
    {
        var settings = Substitute.For<ISettingsManager>();
        settings.Get<string>(Arg.Any<Setting<string>>()).Returns("""{"layout":{"style":"text","width":"wide"},"show":{"cover":false}}""");

        var config = new NowPlayingConfig(settings);

        Assert.Equal("text", config.GetString("layout.style"));
        Assert.Equal(360, config.GetNumber("layout.width"));
        Assert.False(config.GetBool("show.cover"));
        Assert.True(config.GetBool("show.artist"));
    }

    [Fact]
    public void TheLookHasEveryLayoutOption_WithDefaultsThatMatchTheClassicCard()
    {
        var config = Config();

        Assert.Contains("vertical", NowPlayingConfig.Styles);
        Assert.Contains("strip", NowPlayingConfig.Styles);
        Assert.Equal("square", config.GetString("layout.coverShape"));
        Assert.Equal(90, config.GetNumber("layout.coverSize"));
        Assert.Equal(8, config.GetNumber("layout.gap"));
        Assert.Equal(1, config.GetNumber("layout.textSize"));
        Assert.Equal("left", config.GetString("layout.align"));
        Assert.True(config.GetBool("effects.glow"));
        Assert.False(config.GetBool("effects.spin"));
        Assert.Equal(0, config.GetNumber("effects.borderWidth"));
    }

    [Fact]
    public void State_CarriesTheLayoutOptionsToTheOBSPage()
    {
        var config = Config();
        config.Set("layout.style", JsonValue.Create("vertical"));
        config.Set("layout.coverShape", JsonValue.Create("circle"));
        config.Set("effects.spin", JsonValue.Create(true));
        config.Set("effects.borderWidth", JsonValue.Create(3));

        using var state = JsonDocument.Parse(NowPlayingPage.State(new NowPlayingInfo("S", "A", "", "", true, null), config, Noon));
        var look = state.RootElement.GetProperty("look");

        Assert.Equal("vertical", look.GetProperty("style").GetString());
        Assert.Equal("circle", look.GetProperty("coverShape").GetString());
        Assert.True(look.GetProperty("spin").GetBoolean());
        Assert.Equal(3, look.GetProperty("borderWidth").GetDouble());
        Assert.Equal(90, look.GetProperty("coverSize").GetDouble());
    }

    [Fact]
    public void ArtistLine_JoinsTheArtistAndTheAlbumThatAreSwitchedOn()
    {
        var config = Config();
        var info = new NowPlayingInfo("S", "Artist", "Album", "", true, null);

        Assert.Equal("Artist", config.ArtistLine(info));

        config.Set("show.album", JsonValue.Create(true));
        Assert.Equal("Artist · Album", config.ArtistLine(info));

        config.Set("show.artist", JsonValue.Create(false));
        Assert.Equal("Album", config.ArtistLine(info));

        Assert.Equal("Artist", Config().ArtistLine(info with { Album = "" }));
    }

    [Fact]
    public void TheNewOptionsStartAtTheClassicLook()
    {
        var config = Config();

        Assert.Equal("default", config.GetString("layout.fontFamily"));
        Assert.Equal("bold", config.GetString("layout.titleWeight"));
        Assert.False(config.GetBool("layout.uppercase"));
        Assert.Equal(22, config.GetNumber("layout.barCount"));
        Assert.Equal(5, config.GetNumber("layout.progressHeight"));
        Assert.Equal("bottom", config.GetString("layout.progressPosition"));
        Assert.True(config.GetBool("show.paused"));
        Assert.False(config.GetBool("show.album"));
        Assert.False(config.GetBool("show.remaining"));
        Assert.Equal("#FFFFFF", config.GetString("colors.bars"));
        Assert.False(config.GetBool("effects.textShadow"));
        Assert.False(config.GetBool("effects.cardShadow"));
        Assert.False(config.GetBool("effects.gradient"));
    }

    [Fact]
    public void State_CarriesTheNewOptionsAndTheAlbumLineToTheOBSPage()
    {
        var config = Config();
        config.Set("show.album", JsonValue.Create(true));
        config.Set("layout.fontFamily", JsonValue.Create("Georgia"));
        config.Set("layout.progressPosition", JsonValue.Create("top"));
        config.Set("layout.barCount", JsonValue.Create(30));
        config.Set("effects.gradient", JsonValue.Create(true));

        using var state = JsonDocument.Parse(NowPlayingPage.State(new NowPlayingInfo("S", "A", "B", "", true, null), config, Noon));
        var look = state.RootElement.GetProperty("look");

        Assert.Equal("A · B", state.RootElement.GetProperty("artist").GetString());
        Assert.Equal("Georgia", look.GetProperty("fontFamily").GetString());
        Assert.Equal("top", look.GetProperty("progressPosition").GetString());
        Assert.Equal(30, look.GetProperty("barCount").GetInt32());
        Assert.True(look.GetProperty("gradient").GetBoolean());
        Assert.False(look.GetProperty("textShadow").GetBoolean());
    }

    [Theory]
    [InlineData("#abc", true)]
    [InlineData("#A1B2C3", true)]
    [InlineData("abc", false)]
    [InlineData("#abcd", false)]
    [InlineData("#ggg", false)]
    public void IsHex_AcceptsShortAndLongColours(string text, bool expected) => Assert.Equal(expected, NowPlayingConfig.IsHex(text));

    [Fact]
    public void CardColor_IsTheCoversUnlessACustomColourIsPicked()
    {
        var config = Config();
        Assert.Equal(CoverColor.Neutral, config.CardColor(null));

        config.Set("colors.source", JsonValue.Create("custom"));
        config.Set("colors.custom", JsonValue.Create("#3a6df0"));
        Assert.Equal("#3A6DF0", config.CardColor(null));
    }

    [Fact]
    public void CoverColor_TakesTheColourOfThePicture_DarkEnoughForWhiteText()
    {
        using var image = new Image<Rgba32>(16, 16, new Rgba32(250, 30, 30));
        using var memory = new MemoryStream();
        image.SaveAsPng(memory);

        var color = CoverColor.From(memory.ToArray());

        Assert.Matches("^#[0-9A-F]{6}$", color);
        var red = Convert.ToInt32(color[1..3], 16);
        var green = Convert.ToInt32(color[3..5], 16);
        Assert.InRange(red, 70, 150);
        Assert.True(red > green * 3, $"{color} should be clearly red");
    }

    [Fact]
    public void CoverColor_FallsBackToTheNeutralColourWithoutAReadablePicture()
    {
        Assert.Equal(CoverColor.Neutral, CoverColor.From(null));
        Assert.Equal(CoverColor.Neutral, CoverColor.From([]));
        Assert.Equal(CoverColor.Neutral, CoverColor.From([1, 2, 3, 4]));
    }

    [Fact]
    public void TheReaderRunsOnlyWhileSomethingUsesIt_AndTheSongClearsWhenItStops()
    {
        var time = new ManualTime();
        var reader = new FakeReader();
        using var service = new NowPlayingService(reader, time);
        var changes = 0;
        service.Changed += () => changes++;

        var first = service.Use();
        var second = service.Use();
        reader.Emit(new NowPlayingInfo("Song", "Artist", "", "Spotify", true, null));
        Assert.Equal("Song", service.Current?.Title);

        first.Dispose();
        Assert.Equal(0, reader.Stops);
        second.Dispose();
        second.Dispose();
        Assert.Equal(1, reader.Stops);
        Assert.Null(service.Current);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void AProblemWithTheMediaReader_ReachesTheServiceAndTellsEveryCardToRedraw()
    {
        var reader = new FakeReader();
        using var service = new NowPlayingService(reader, new ManualTime());
        var redraws = 0;
        service.Changed += () => redraws++;

        reader.Fail("PowerShell is blocked");
        Assert.Equal("PowerShell is blocked", service.Problem);

        reader.Fail(null);
        Assert.Null(service.Problem);
        Assert.Equal(2, redraws);
    }

    [Fact]
    public void DescribeExit_NamesTheCodeAndKeepsTheLastErrorShort()
    {
        var plain = WindowsMediaSessionReader.DescribeExit(1, null);
        var detailed = WindowsMediaSessionReader.DescribeExit(5, new string('x', 400));

        Assert.Contains("code 1", plain);
        Assert.Contains("blocking", plain);
        Assert.DoesNotContain("(", plain.Split("blocking it.")[1]);
        Assert.Contains("code 5", detailed);
        Assert.True(detailed.Length < 400);
    }

    [Fact]
    public void ARequestFromOutsideKeepsTheReaderRunningForAWhile_ThenItStopsOnItsOwn()
    {
        var time = new ManualTime();
        var reader = new FakeReader();
        using var service = new NowPlayingService(reader, time);

        service.Touch();
        time.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(0, reader.Stops);

        time.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(1, reader.Stops);
    }

    private static NowPlayingConfig Config()
    {
        var settings = Substitute.For<ISettingsManager>();
        settings.Set(Arg.Any<Setting<string>>(), Arg.Any<string>()).Returns(true);
        return new NowPlayingConfig(settings);
    }

    private static bool Visible(string state)
    {
        using var document = JsonDocument.Parse(state);
        return document.RootElement.GetProperty("visible").GetBoolean();
    }

    private sealed class FakeReader : IMediaSessionReader
    {
        public int Stops { get; private set; }
        public bool IsSupported => true;
        public string? Problem { get; private set; }
        public event Action<NowPlayingInfo?>? Changed;
        public event Action? ProblemChanged;

        public void Fail(string? problem)
        {
            Problem = problem;
            ProblemChanged?.Invoke();
        }

        public void Start() { }

        public void Stop() => Stops++;

        public void Emit(NowPlayingInfo? info) => Changed?.Invoke(info);

        public void Dispose() { }
    }

    // A clock that moves only when told; its timer fires its callback on every Advance, like the 10 second check does.
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = Noon;
        private readonly List<Action> _timers = [];

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _timers.Add(() => callback(state));
            return new Handle();
        }

        public void Advance(TimeSpan by)
        {
            _now += by;
            foreach (var timer in _timers.ToList())
                timer();
        }

        private sealed class Handle : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() { }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
