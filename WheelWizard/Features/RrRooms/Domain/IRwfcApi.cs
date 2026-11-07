using Refit;
using WheelWizard.Ghosts;
using WheelWizard.Models;

namespace WheelWizard.RrRooms;

public interface IRwfcApi
{
    [Get("/api/roomstatus")]
    Task<RwfcRoomStatusResponse> GetRoomStatusAsync();

    [Get("/api/leaderboard/top/{limit}")]
    Task<List<RwfcLeaderboardEntry>> GetTopLeaderboardAsync(int limit);

    [Get("/api/leaderboard/player/{friendCode}")]
    Task<PlayerProfileResponse> GetPlayerProfileAsync(string friendCode);

    [Get("/api/leaderboard")]
    Task<RwfcLeaderboardSearchResponse> SearchLeaderboardAsync([AliasAs("search")] string search);

    [Get("/api/leaderboard")]
    Task<RwfcLeaderboardSearchResponse> GetLeaderboardPageAsync([AliasAs("page")] int page);

    [Get("/api/leaderboard/player/{friendCode}/history")]
    Task<RwfcPlayerVrHistoryResponse> GetPlayerVrHistoryAsync(string friendCode, [AliasAs("days")] int days);

    // Time trials (rwfc.net/tt). A null filter is left out, which means "any".
    [Get("/api/timetrial/tracks")]
    Task<List<RwfcTimeTrialTrack>> GetTimeTrialTracksAsync();

    /// <summary>Every track with its current world record for these filters.</summary>
    [Get("/api/timetrial/worldrecords/all")]
    Task<List<RwfcTrackRecord>> GetTimeTrialRecordsAsync(
        [AliasAs("cc")] int cc,
        [AliasAs("glitchAllowed")] bool? glitchAllowed,
        [AliasAs("shroomless")] string? shroomless,
        [AliasAs("vehicle")] string? vehicle
    );

    /// <summary>Players ranked by the world records they hold; <paramref name="trackCategory"/> is "retro", "custom" or null for both.</summary>
    [Get("/api/timetrial/rankings")]
    Task<RwfcTimeTrialRankings> GetTimeTrialRankingsAsync(
        [AliasAs("cc")] int cc,
        [AliasAs("glitchAllowed")] bool? glitchAllowed,
        [AliasAs("shroomless")] string? shroomless,
        [AliasAs("vehicle")] string? vehicle,
        [AliasAs("trackCategory")] string? trackCategory,
        [AliasAs("page")] int page,
        [AliasAs("pageSize")] int pageSize
    );

    [Get("/api/timetrial/profile/{id}")]
    Task<RwfcTimeTrialProfile> GetTimeTrialProfileAsync(int id);

    /// <summary>A player's times; a null filter means "any". At most 100 per page.</summary>
    [Get("/api/timetrial/profile/{id}/submissions")]
    Task<RwfcProfileSubmissions> GetTimeTrialProfileSubmissionsAsync(
        int id,
        [AliasAs("cc")] int? cc,
        [AliasAs("glitch")] bool? glitch,
        [AliasAs("shroomless")] string? shroomless,
        [AliasAs("vehicle")] string? vehicle,
        [AliasAs("page")] int page,
        [AliasAs("pageSize")] int pageSize
    );

    [Get("/api/timetrial/leaderboard")]
    Task<RwfcTimeTrialBoard> GetTimeTrialBoardAsync(
        [AliasAs("trackId")] int trackId,
        [AliasAs("cc")] int cc,
        [AliasAs("glitchAllowed")] bool? glitchAllowed,
        [AliasAs("shroomless")] string? shroomless,
        [AliasAs("vehicle")] string? vehicle,
        [AliasAs("page")] int page,
        [AliasAs("pageSize")] int pageSize
    );

    /// <summary>The same board ranked by each run's fastest lap.</summary>
    [Get("/api/timetrial/leaderboard/flap")]
    Task<RwfcTimeTrialBoard> GetTimeTrialLapBoardAsync(
        [AliasAs("trackId")] int trackId,
        [AliasAs("cc")] int cc,
        [AliasAs("glitchAllowed")] bool? glitchAllowed,
        [AliasAs("shroomless")] string? shroomless,
        [AliasAs("vehicle")] string? vehicle,
        [AliasAs("page")] int page,
        [AliasAs("pageSize")] int pageSize
    );

    /// <summary>The ghost file (.rkg) of a time on the board.</summary>
    [Get("/api/timetrial/ghost/{id}/download")]
    Task<HttpContent> DownloadTimeTrialGhostAsync(int id);

    [Get("/api/badges/all")]
    Task<RwfcBadgesResponse> GetAllBadgesAsync();
}
