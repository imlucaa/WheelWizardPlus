using System.IO.Abstractions;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WheelWizard.CustomDistributions;
using WheelWizard.Dolphin.Paths;
using WheelWizard.Models.Enums;
using WheelWizard.RrRooms;
using WheelWizard.Settings;
using WheelWizard.Settings.Types;
using WheelWizard.Shared.Binary;
using WheelWizard.Shared.IO;
using WheelWizard.Shared.Polling;
using WheelWizard.WheelWizardData;
using WheelWizard.WiiManagement.FriendCodes;
using WheelWizard.WiiManagement.GameLicense.Domain;
using WheelWizard.WiiManagement.MiiManagement;
using WheelWizard.WiiManagement.MiiManagement.Domain.Mii;

namespace WheelWizard.WiiManagement.GameLicense;

// big big thanks to https://kazuki-4ys.github.io/web_apps/FaceThief/ for the JS implementation
// Also Refer to this documentation https://wiki.tockdom.com/wiki/Rksys.dat
public sealed record RegionLicense(MarioKartWiiEnums.Regions Region, int Slot, string Name, string FriendCode);

/// <summary>The three ids that identify a license on the server. They must be unique per license.</summary>
/// <summary>A copy of the save kept in the backup folder. Kind says why it was made: manual, change, undo, before-restore or before-import.</summary>
public sealed record SaveBackup(string Name, string Kind, DateTime Time, string FilePath, string? Label = null);

/// <summary>A license found inside a backup, so it can be shown before the backup is restored.</summary>
public sealed record BackupLicense(int Slot, string Name, string FriendCode, uint PseudoId = 0, uint AuthenticId = 0);

public sealed record LicenseIdentity(int Slot, string Name, uint ProfileId, string FriendCode, uint PseudoId, uint AuthenticId);

/// <summary>
/// One license whose friend code changed. <paramref name="Kind"/> is the kind of backup taken just before it (for example
/// "change" or "before-license-import"), and <paramref name="BackupName"/> that backup, which still holds the old code. A
/// code is empty when the slot had no license.
/// </summary>
public sealed record FriendCodeChange(DateTime Time, string Kind, int Slot, string Name, string OldCode, string NewCode, string BackupName);

/// <summary>
/// What is wrong with the ids of one license, the things a duplicate checker looks for: another license that shares its
/// friend code, pseudo id or authentic id (the server then cannot tell them apart, and one may not save), and pseudo and
/// authentic ids that differ (they have to match).
/// </summary>
public sealed record LicenseIdProblems(IReadOnlyList<LicenseIdentity> SharesWith, bool IdsDiffer)
{
    public bool Any => SharesWith.Count > 0 || IdsDiffer;

    /// <summary>Another license has the very same friend code, which new ids cannot fix.</summary>
    public bool SameFriendCode(LicenseIdentity license) => SharesWith.Any(other => other.ProfileId == license.ProfileId);
}

public static class LicenseIds
{
    public static LicenseIdProblems Check(LicenseIdentity license, IEnumerable<LicenseIdentity> all) =>
        new(
            all.Where(other =>
                    other.Slot != license.Slot
                    && (
                        other.ProfileId == license.ProfileId
                        || other.PseudoId == license.PseudoId
                        || other.AuthenticId == license.AuthenticId
                    )
                )
                .ToList(),
            license.PseudoId != license.AuthenticId
        );

    /// <summary>The next value above both ids that none of the other licenses use, to give a license new shared ids.</summary>
    public static uint NextFree(uint pseudo, uint authentic, IEnumerable<LicenseIdentity> others)
    {
        var used = others.SelectMany(license => new[] { license.PseudoId, license.AuthenticId }).ToHashSet();
        var next = Math.Max(pseudo, authentic) + 1;
        while (next == 0 || used.Contains(next))
            next++;
        return next;
    }
}

/// <summary>What a .rkp license file holds: the license name and friend code.</summary>
public sealed record LicenseFile(string Name, string FriendCode, uint ProfileId, uint PseudoId = 0, uint AuthenticId = 0);

public interface IGameLicenseSingletonService
{
    /// <summary>
    /// Gets the currently loaded <see cref="Domain.LicenseCollection"/>.
    /// </summary>
    LicenseCollection LicenseCollection { get; }

    /// <summary>
    /// Loads the game data from the rksys.dat file.
    /// </summary>
    OperationResult LoadLicense();

    IReadOnlyList<RegionLicense> GetRegionLicenses();

    /// <summary>
    /// Retrieves the user data for a specific index.
    /// </summary>
    /// <param name="index">Index of user (1-3)</param>
    LicenseProfile GetUserData(int index);

    /// <summary>
    /// Gets the currently selected user.
    /// </summary>
    LicenseProfile ActiveUser { get; }

    /// <summary>
    /// Gets the list of friends for the currently selected user.
    /// </summary>
    List<FriendProfile> ActiveCurrentFriends { get; }

    /// <summary>
    /// Checks if any user is valid (i.e., has a non-empty friend code).
    /// </summary>
    bool HasAnyValidUsers { get; }

    /// <summary>
    /// Refreshes the online status of the users based on the current live rooms.
    /// </summary>
    void RefreshOnlineStatus();

    /// <summary>When the selected region's rksys.dat was last written, or null when there is none to read.</summary>
    DateTime? GetSaveWriteTimeUtc();

    /// <summary>
    /// Changes the name of a Mii for a specific user index.
    /// </summary>
    OperationResult ChangeMiiName(int userIndex, string newName);

    /// <summary>
    /// Subscribes a listener to the repeated task manager.
    /// </summary>
    void Subscribe(IPollingListener subscriber);
    bool Unsubscribe(IPollingListener subscriber);

    /// <summary>
    /// Changes the Mii for a specific user index.
    /// </summary>
    OperationResult ChangeMii(int userIndex, Mii? newMii);

    /// <summary>
    /// Adds a friend to the specified license slot in rksys.dat.
    /// </summary>
    OperationResult AddFriend(int userIndex, string friendCode, Mii friendMii, uint vr = 5000, uint br = 5000);

    /// <summary>
    /// Removes a friend from the specified license slot in rksys.dat.
    /// </summary>
    OperationResult RemoveFriend(int userIndex, string friendCode);

    /// <summary>The profile, pseudo and authentic ids of every used license, for finding duplicates.</summary>
    IReadOnlyList<LicenseIdentity> GetLicenseIdentities();

    /// <summary>
    /// Gives a license a new profile id (so a new friend code). The pseudo and authentic ids are raised to a new shared value
    /// no other license uses. A backup of rksys.dat is made first.
    /// </summary>
    OperationResult ChangeProfileId(int userIndex, uint newProfileId);

    /// <summary>
    /// Gives a license new pseudo and authentic ids (one shared value no other license uses) and keeps its friend code. It fixes
    /// a license that shares ids with another or whose two ids differ. A backup of rksys.dat is made first.
    /// </summary>
    OperationResult MakeIdsUnique(int userIndex);

    /// <summary>Copies the current rksys.dat into its own dated folder inside "backup". Returns the folder name.</summary>
    OperationResult<string> BackupSaveNow();

    /// <summary>The backups in the backup folder, newest first.</summary>
    IReadOnlyList<SaveBackup> ListBackups();

    /// <summary>Gives a backup a name you will recognise, or clears the name when it is empty. A named backup is never removed on its own.</summary>
    OperationResult NameBackup(string name, string label);

    /// <summary>Deletes one backup.</summary>
    OperationResult DeleteBackup(string name);

    /// <summary>
    /// Deletes the oldest backups, keeping the newest <paramref name="keep"/>. Named backups and the ones that record a
    /// friend code change (the history) are kept and not counted. Returns how many were deleted.
    /// </summary>
    int PruneBackups(int keep);

    /// <summary>The licenses (name and friend code) stored in a backup.</summary>
    IReadOnlyList<BackupLicense> GetBackupLicenses(string name);

    /// <summary>Puts a whole backup back as the save. The current save is backed up first.</summary>
    OperationResult RestoreBackup(string name);

    /// <summary>Copies the current rksys.dat to the given path.</summary>
    OperationResult ExportSave(string destinationPath);

    /// <summary>Replaces the save with a rksys.dat from the given path. The current save is backed up first.</summary>
    OperationResult ImportSave(string sourcePath);

    /// <summary>Saves one license as a .rkp file: its RKPD block from rksys.dat, the way other save tools share licenses.</summary>
    OperationResult ExportLicense(int userIndex, string destinationPath);

    /// <summary>Reads a .rkp file without changing anything, to show what is in it.</summary>
    OperationResult<LicenseFile> ReadLicenseFile(string sourcePath);

    /// <summary>Puts a .rkp license into a slot of the save, replacing what is there. The save is backed up first.</summary>
    OperationResult ImportLicense(int userIndex, string sourcePath);

    /// <summary>The licenses (slot, name and friend code) in any rksys.dat file, such as a backup. Empty when it is not a save.</summary>
    IReadOnlyList<BackupLicense> ReadSaveLicenses(string savePath);

    /// <summary>
    /// Puts one license of another rksys.dat (a backup, or a save from somewhere else) into a slot of this save, replacing
    /// what is there. The rest of that save is not used. This save is backed up first.
    /// </summary>
    OperationResult ImportLicenseFromSave(int userIndex, string savePath, int sourceSlot);

    /// <summary>
    /// Puts several licenses of another rksys.dat back into this save, each into its own slot (license 2 of the other save
    /// replaces license 2 here). All of them are written together after one backup; if one cannot be taken, none is.
    /// </summary>
    OperationResult ImportLicensesFromSave(string savePath, IReadOnlyList<int> slots);

    /// <summary>
    /// Puts several .rkp files into the given slots (slot, file) after one backup. Every file is checked first; if one is
    /// not a license file, nothing is written.
    /// </summary>
    OperationResult ImportLicenseFiles(IReadOnlyList<(int Slot, string Path)> files);

    /// <summary>When the newest backup made by <see cref="ChangeProfileId"/> was taken, or null when there is none.</summary>
    DateTime? GetLastBackupTime();

    /// <summary>
    /// Every friend code change on this region's save, newest first. Worked out from the backups kept before each change, so
    /// it also covers changes made before this list existed.
    /// </summary>
    IReadOnlyList<FriendCodeChange> GetFriendCodeHistory();

    /// <summary>
    /// Puts back the friend code a license had in a backup (the old code of a <see cref="FriendCodeChange"/>), on every
    /// region's save. The saves are backed up first, and "undo last change" takes it back.
    /// </summary>
    OperationResult RestoreFriendCode(string backupName, int slot);

    /// <summary>
    /// Puts the profile, pseudo and authentic ids back to what the newest backup has. Everything else in the save (races, VR,
    /// Miis) stays as it is now.
    /// </summary>
    OperationResult UndoLastProfileIdChange();
}

public class GameLicenseSingletonService : ObservablePollingService, IGameLicenseSingletonService
{
    private readonly IMiiDbService _miiService;
    private readonly IFileSystem _fileSystem;
    private readonly IWhWzDataSingletonService _whWzDataSingletonService;
    private readonly IRrRatingReader _rrratingReader;
    private readonly ISettingsManager _settingsManager;
    private readonly ISaveRegionService _saveRegions;
    private readonly IDolphinPaths _dolphinPaths;
    private readonly ICustomDistributionPaths _distributionPaths;
    private readonly IRoomPresence _presence;
    private LicenseCollection Licenses { get; }
    private byte[]? _rksysData;

    public GameLicenseSingletonService(
        IMiiDbService miiService,
        IFileSystem fileSystem,
        IWhWzDataSingletonService whWzDataSingletonService,
        IRrRatingReader rrratingReader,
        ISettingsManager settingsManager,
        ISaveRegionService saveRegions,
        IDolphinPaths dolphinPaths,
        ICustomDistributionPaths distributionPaths,
        IRoomPresence presence,
        IPollingScheduler scheduler,
        TimeProvider timeProvider,
        ILogger<GameLicenseSingletonService> logger
    )
        : base(40, scheduler, timeProvider, logger)
    {
        _miiService = miiService;
        _fileSystem = fileSystem;
        _whWzDataSingletonService = whWzDataSingletonService;
        _rrratingReader = rrratingReader;
        _settingsManager = settingsManager;
        _saveRegions = saveRegions;
        _dolphinPaths = dolphinPaths;
        _distributionPaths = distributionPaths;
        _presence = presence;
        Licenses = new();
    }

    private const int RksysSize = 0x2BC000;
    private const string RksysMagic = "RKSD0006";
    private const int MaxPlayerNum = 4;
    private const int RkpdSize = 0x8CC0;
    private const string RkpdMagic = "RKPD";
    private const int MaxFriendNum = 30;
    private const int FriendDataOffset = 0x56D0;
    private const int FriendDataSize = 0x1C0;
    private const int FriendSecondaryDataOffset = 0x8B50;
    private const int FriendSecondaryDataSize = 0x0C;
    private const int MiiSize = 0x4A;

    // Pending one-sided friend entry (bit0 set, bit1 clear).
    private const ushort FriendSlotStateAdded = 0x0001;
    private const byte DwcFriendControlTypeInvalid = 0x00;
    private const byte DwcFriendControlTypeFriendKey = 0x10;
    private const ushort DefaultFriendCityId = 0;
    private const byte DefaultFriendGameRegion = 0;
    private const byte DefaultFriendCountryCode = 0xFF;
    private const byte DefaultFriendRegionId = 0xFF;

    /// <summary>
    /// Returns the "focused" or currently active license/user as determined by the Settings.
    /// </summary>
    public LicenseProfile ActiveUser => Licenses.Users[_settingsManager.Get<int>(_settingsManager.FOCUSED_USER)];

    public List<FriendProfile> ActiveCurrentFriends => Licenses.Users[_settingsManager.Get<int>(_settingsManager.FOCUSED_USER)].Friends;

    public LicenseCollection LicenseCollection => Licenses;

    public LicenseProfile GetUserData(int index) => Licenses.Users[index];

    public bool HasAnyValidUsers => Licenses.Users.Any(user => user.FriendCode != "0000-0000-0000");

    public DateTime? GetSaveWriteTimeUtc()
    {
        try
        {
            var path = _fileSystem.Path.Combine(SaveFolderForRegion(), "rksys.dat");
            return _fileSystem.File.Exists(path) ? _fileSystem.File.GetLastWriteTimeUtc(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    public void RefreshOnlineStatus()
    {
        foreach (var user in Licenses.Users)
        {
            user.IsOnline = _presence.IsOnline(user.FriendCode);
            foreach (var friend in user.Friends)
            {
                friend.IsOnline = _presence.IsOnline(friend.FriendCode);
                // The save keeps a friend's ratings as they were when you last saw them; in a room they are current.
                if (friend.IsOnline && _presence.TryGetRating(friend.FriendCode, out var vr, out var br))
                {
                    if (vr is { } liveVr and >= 0)
                        friend.Vr = (uint)liveVr;
                    if (br is { } liveBr and >= 0)
                        friend.Br = (uint)liveBr;
                }
            }
        }
    }

    // Read menu summaries without changing the active region or loaded save data.
    public IReadOnlyList<RegionLicense> GetRegionLicenses()
    {
        var result = new List<RegionLicense>();
        foreach (var region in Enum.GetValues<MarioKartWiiEnums.Regions>().Where(r => r != MarioKartWiiEnums.Regions.None))
        {
            try
            {
                var path = _fileSystem.Path.Combine(_distributionPaths.SaveFolderPath, GameRegion.GetGameId(region), "rksys.dat");
                if (!_fileSystem.File.Exists(path))
                    continue;
                var data = _fileSystem.File.ReadAllBytes(path);
                if (data.Length != RksysSize || Encoding.ASCII.GetString(data, 0, RksysMagic.Length) != RksysMagic)
                    continue;
                for (var slot = 0; slot < MaxPlayerNum; slot++)
                {
                    var offset = RksysMagic.Length + slot * RkpdSize;
                    if (Encoding.ASCII.GetString(data, offset, RkpdMagic.Length) != RkpdMagic)
                        continue;
                    var friendCode = FriendCode.GetFriendCode(data, offset + 0x5C);
                    if (string.IsNullOrEmpty(friendCode) || friendCode == "0000-0000-0000")
                        continue;
                    var mii = _miiService.GetByAvatarId(BigEndianBinary.BufferToUint32(data, offset + 0x28));
                    var name = mii.IsSuccess ? mii.Value.Name.ToString() : BigEndianBinary.GetUtf16String(data, offset + 0x14, 10);
                    result.Add(new(region, slot, name, friendCode));
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return result;
    }

    public OperationResult LoadLicense()
    {
        var loadSaveDataResult = ReadRksys();
        _rksysData = loadSaveDataResult.IsSuccess ? loadSaveDataResult.Value : null;
        if (_rksysData != null && ValidateMagicNumber())
        {
            return ParseUsers();
        }

        // If the file was invalid or not found, create 4 dummy licenses
        Licenses.Users.Clear();
        for (var i = 0; i < MaxPlayerNum; i++)
            Licenses.Users.Add(CreateDummyLicense());
        return Ok();
    }

    private static LicenseProfile CreateDummyLicense()
    {
        var dummyLicense = new LicenseProfile
        {
            FriendCode = "0000-0000-0000",
            Mii = new() { Name = new MiiName(SettingValues.NoLicense) },
            Vr = 5000,
            Br = 5000,
            TotalRaceCount = 0,
            TotalWinCount = 0,
            Friends = [],
            RegionId = 10, // 10 => “unknown”
            IsOnline = false,
            Statistics = new(),
        };
        return dummyLicense;
    }

    private OperationResult ParseUsers()
    {
        Licenses.Users.Clear();
        if (_rksysData == null)
            return new ArgumentNullException(nameof(_rksysData));

        for (var i = 0; i < MaxPlayerNum; i++)
        {
            var rkpdOffset = RksysMagic.Length + i * RkpdSize;
            var rkpdCheck = Encoding.ASCII.GetString(_rksysData, rkpdOffset, RkpdMagic.Length) == RkpdMagic;
            if (!rkpdCheck)
            {
                Licenses.Users.Add(CreateDummyLicense());
                continue;
            }

            var user = ParseLicenseUser(rkpdOffset);
            if (user.IsFailure)
            {
                Licenses.Users.Add(CreateDummyLicense());
                continue;
            }
            Licenses.Users.Add(user.Value);
        }

        // Keep this here so we always have 4 users if the code above were to be changed
        while (Licenses.Users.Count < 4)
        {
            Licenses.Users.Add(CreateDummyLicense());
        }
        RefreshOnlineStatus();
        return Ok();
    }

    private OperationResult<LicenseProfile> ParseLicenseUser(int rkpdOffset)
    {
        if (_rksysData == null)
            return new ArgumentNullException(nameof(_rksysData));

        var profileId = BigEndianBinary.BufferToUint32(_rksysData, rkpdOffset + 0x5C);
        var friendCode = FriendCode.GetFriendCode(_rksysData, rkpdOffset + 0x5C);
        var miiDataResult = ParseMiiData(rkpdOffset);
        var miiToUse = miiDataResult.IsFailure ? new() : miiDataResult.Value;

        var statistics = StatisticsSerializer.ParseStatistics(_rksysData, rkpdOffset);

        // Try to read VR/BR from RRRating.pul file
        var vrFromRksys = BigEndianBinary.BufferToUint16(_rksysData, rkpdOffset + 0xB0);
        var brFromRksys = BigEndianBinary.BufferToUint16(_rksysData, rkpdOffset + 0xB2);
        var vr = vrFromRksys;
        var br = brFromRksys;

        if (profileId > 0 && !string.IsNullOrEmpty(friendCode))
        {
            var rrRatingData = TryReadRRratingFile();
            if (rrRatingData != null)
            {
                // Try to find by profile_id first
                var rating = _rrratingReader.ReadRatingFromFile(rrRatingData, profileId);

                // Verify friend code matches if rating found
                if (rating.HasValue)
                {
                    // Calculate friend code from profile_id and compare with rksys friend code
                    var calculatedFriendCode = FriendCode.ProfileIdToFriendCode(profileId);
                    // Convert friend code string to ulong for comparison
                    var fcString = friendCode.Replace("-", "");
                    if (ulong.TryParse(fcString, out var fcDec))
                    {
                        // Compare friend codes - use rating if they match
                        if (fcDec == calculatedFriendCode)
                        {
                            // Convert float VR/BR to uint by multiplying by 100 (e.g., 258.62 -> 25862)
                            vr = (uint)Math.Round(rating.Value.vr * 100);
                            br = (uint)Math.Round(rating.Value.br * 100);
                        }
                    }
                }
            }
        }

        var user = new LicenseProfile
        {
            Mii = miiToUse,
            FriendCode = friendCode,
            Vr = vr,
            Br = br,
            TotalRaceCount = BigEndianBinary.BufferToUint32(_rksysData, rkpdOffset + 0xB4),
            TotalWinCount = BigEndianBinary.BufferToUint32(_rksysData, rkpdOffset + 0xDC),
            BadgeVariants = _whWzDataSingletonService.GetBadges(friendCode),
            // Region is often found near offset 0x23308 + 0x3802 in RKGD. This code is a partial guess.
            // In practice, region might be read differently depending on your rksys layout.
            RegionId = BigEndianBinary.BufferToUint16(_rksysData, 0x23308 + 0x3802) / 4096,
            Statistics = statistics,
        };

        ParseFriends(user, rkpdOffset);
        return user;
    }

    private byte[]? TryReadRRratingFile()
    {
        try
        {
            var rrRatingPath = _fileSystem.Path.GetRrRatingFilePath(_dolphinPaths.WiiFolderPath);
            if (_fileSystem.File.Exists(rrRatingPath))
            {
                return _fileSystem.File.ReadAllBytes(rrRatingPath);
            }
        }
        catch
        {
            // Silently fail if file doesn't exist or can't be read
        }
        return null;
    }

    private OperationResult<Mii> ParseMiiData(int rkpdOffset)
    {
        //https://wiki.tockdom.com/wiki/Rksys.dat#DWC_User_Data
        if (_rksysData == null)
            return new ArgumentNullException(nameof(_rksysData));

        // licenseName is NOT always the same as mii name, could be useful
        var licenseName = BigEndianBinary.GetUtf16String(_rksysData, rkpdOffset + 0x14, 10);
        // id of mii
        var avatarId = BigEndianBinary.BufferToUint32(_rksysData, rkpdOffset + 0x28);
        // id of the actual system
        var clientId = BigEndianBinary.BufferToUint32(_rksysData, rkpdOffset + 0x2C);

        var rawMiiResult = _miiService.GetByAvatarId(avatarId);
        if (rawMiiResult.IsFailure)
            return new FormatException("Failed to parse mii data: " + rawMiiResult.Error.Message);

        return rawMiiResult.Value;
    }

    private void ParseFriends(LicenseProfile licenseProfile, int userOffset)
    {
        if (_rksysData == null)
            return;

        var friendOffset = userOffset + FriendDataOffset;
        for (var i = 0; i < MaxFriendNum; i++)
        {
            var currentOffset = friendOffset + i * FriendDataSize;
            if (!CheckForMiiData(currentOffset + 0x1A))
                continue;

            var statusFlags = (ushort)BigEndianBinary.BufferToUint16(_rksysData, currentOffset + 0x10);
            var baseSlotState = (ushort)(statusFlags & 0x0003);
            var secondaryOffset = userOffset + FriendSecondaryDataOffset + i * FriendSecondaryDataSize;
            var secondaryControlByte = _rksysData[secondaryOffset + 0x2];
            // Treat legacy one-sided entries with control byte 0x00 as pending as well.
            var isPending =
                baseSlotState == FriendSlotStateAdded
                && (secondaryControlByte == DwcFriendControlTypeFriendKey || secondaryControlByte == DwcFriendControlTypeInvalid);

            var rawMiiBytes = _rksysData.AsSpan(currentOffset + 0x1A, MiiSize).ToArray();
            var friendCode = FriendCode.GetFriendCode(_rksysData, currentOffset + 4);
            var miiResult = MiiSerializer.Deserialize(rawMiiBytes);
            if (miiResult.IsFailure)
                continue;

            var friend = new FriendProfile
            {
                Vr = BigEndianBinary.BufferToUint16(_rksysData, currentOffset + 0x16),
                Br = BigEndianBinary.BufferToUint16(_rksysData, currentOffset + 0x18),
                FriendCode = friendCode,
                Wins = BigEndianBinary.BufferToUint16(_rksysData, currentOffset + 0x14),
                Losses = BigEndianBinary.BufferToUint16(_rksysData, currentOffset + 0x12),
                CountryCode = _rksysData[currentOffset + 0x68],
                RegionId = _rksysData[currentOffset + 0x69],
                BadgeVariants = _whWzDataSingletonService.GetBadges(friendCode),
                IsPending = isPending,
                Mii = miiResult.Value,
            };
            licenseProfile.Friends.Add(friend);
        }
    }

    public OperationResult ChangeMii(int userIndex, Mii? newMii)
    {
        if (newMii is null)
            return Fail("Mii cannot be null.");
        if (userIndex is < 0 or >= MaxPlayerNum)
            return Fail("Invalid license index. Please select a valid license.");

        var serialised = MiiSerializer.Serialize(newMii);
        if (serialised.IsFailure)
            return serialised.Error;

        var existing = _miiService.GetByAvatarId(newMii.MiiId);
        if (existing.IsFailure)
            return existing.Error;

        var licence = Licenses.Users[userIndex];
        licence.Mii = newMii;

        if (_rksysData is null || _rksysData.Length < RksysSize)
            return Fail("Invalid or unloaded rksys.dat data.");

        var rkpdOffset = 0x08 + userIndex * RkpdSize;
        BigEndianBinary.WriteUInt32BigEndian(_rksysData, rkpdOffset + 0x28, newMii.MiiId); // Avatar ID

        var systemid = newMii.SystemId0 << 24 | newMii.SystemId1 << 16 | newMii.SystemId2 << 8 | newMii.SystemId3;

        BigEndianBinary.WriteUInt32BigEndian(_rksysData, rkpdOffset + 0x2C, (uint)systemid);

        var nameWrite = WriteLicenseNameToSaveData(userIndex, newMii.Name.ToString());
        if (nameWrite.IsFailure)
            return nameWrite.Error;

        var saveResult = SaveRksysToFile();
        if (saveResult.IsFailure)
            return saveResult.Error;

        return Ok();
    }

    public OperationResult AddFriend(int userIndex, string friendCode, Mii friendMii, uint vr = 5000, uint br = 5000)
    {
        if (userIndex is < 0 or >= MaxPlayerNum)
            return Fail("Invalid license index. Please select a valid license.");
        if (friendMii is null)
            return Fail("Friend Mii cannot be null.");
        if (_rksysData is null || _rksysData.Length < RksysSize)
            return Fail("Invalid or unloaded rksys.dat data.");

        var normalizedFriendCode = NormalizeFriendCode(friendCode);
        if (normalizedFriendCode.IsFailure)
            return normalizedFriendCode.Error;

        var friendProfileId = FriendCode.FriendCodeToProfileId(normalizedFriendCode.Value);
        if (friendProfileId == 0)
            return Fail("Invalid friend code.");
        if (!FriendCode.TryParseFriendCode(normalizedFriendCode.Value, out var friendCodeValue))
            return Fail("Invalid friend code.");

        var selectedLicense = Licenses.Users[userIndex];
        var currentUserPid = FriendCode.FriendCodeToProfileId(selectedLicense.FriendCode);
        if (currentUserPid != 0 && currentUserPid == friendProfileId)
            return Fail("You cannot add your own friend code.");

        var duplicateFriend = selectedLicense.Friends.Any(friend =>
        {
            var pid = FriendCode.FriendCodeToProfileId(friend.FriendCode);
            return pid != 0 && pid == friendProfileId;
        });
        if (duplicateFriend)
            return Fail("This friend is already in your list.");

        var friendMiiDataResult = MiiSerializer.Serialize(friendMii);
        if (friendMiiDataResult.IsFailure)
            return friendMiiDataResult.Error;

        var rkpdOffset = RksysMagic.Length + userIndex * RkpdSize;
        var slotIndex = FindFirstEmptyFriendSlot(rkpdOffset);
        if (slotIndex < 0)
            return Fail("Your friend list is full. Remove a friend and try again.");

        WriteFriendSlot(
            rkpdOffset,
            slotIndex,
            friendCodeValue,
            friendProfileId,
            friendMiiDataResult.Value,
            (ushort)Math.Min(vr, ushort.MaxValue),
            (ushort)Math.Min(br, ushort.MaxValue)
        );

        var saveResult = SaveRksysToFile();
        if (saveResult.IsFailure)
            return saveResult.Error;

        return ParseUsers();
    }

    private bool CheckForMiiData(int offset)
    {
        if (_rksysData == null || offset < 0 || offset + MiiSize > _rksysData.Length)
            return false;

        // If the entire 0x4A bytes are zero, we treat it as empty / no Mii data
        for (var i = 0; i < MiiSize; i++)
        {
            if (_rksysData[offset + i] != 0)
                return true;
        }

        return false;
    }

    private int FindFirstEmptyFriendSlot(int rkpdOffset)
    {
        if (_rksysData == null)
            return -1;

        var friendOffset = rkpdOffset + FriendDataOffset;
        for (var i = 0; i < MaxFriendNum; i++)
        {
            var currentOffset = friendOffset + i * FriendDataSize;
            if (IsFriendSlotEmpty(currentOffset))
                return i;
        }

        return -1;
    }

    private bool IsFriendSlotEmpty(int friendSlotOffset)
    {
        if (_rksysData == null)
            return true;

        var statusFlags = (ushort)BigEndianBinary.BufferToUint16(_rksysData, friendSlotOffset + 0x10);
        var profileId = BigEndianBinary.BufferToUint32(_rksysData, friendSlotOffset + 0x04);
        var hasMii = CheckForMiiData(friendSlotOffset + 0x1A);

        return profileId == 0 && (statusFlags & 0x0003) == 0 && !hasMii;
    }

    private void WriteFriendSlot(
        int rkpdOffset,
        int slotIndex,
        ulong friendCodeValue,
        uint friendProfileId,
        byte[] serializedMii,
        ushort vr,
        ushort br
    )
    {
        if (_rksysData == null)
            return;

        var mainOffset = rkpdOffset + FriendDataOffset + slotIndex * FriendDataSize;
        var secondaryOffset = rkpdOffset + FriendSecondaryDataOffset + slotIndex * FriendSecondaryDataSize;

        Array.Clear(_rksysData, mainOffset, FriendDataSize);
        Array.Clear(_rksysData, secondaryOffset, FriendSecondaryDataSize);

        // Friend identity key (high 32 bits + profile ID low 32 bits).
        BigEndianBinary.WriteUInt32BigEndian(_rksysData, mainOffset, (uint)(friendCodeValue >> 32));
        BigEndianBinary.WriteUInt32BigEndian(_rksysData, mainOffset + 0x04, friendProfileId);
        BigEndianBinary.WriteUInt16BigEndian(_rksysData, mainOffset + 0x10, FriendSlotStateAdded);
        BigEndianBinary.WriteUInt16BigEndian(_rksysData, mainOffset + 0x12, 0);
        BigEndianBinary.WriteUInt16BigEndian(_rksysData, mainOffset + 0x14, 0);
        BigEndianBinary.WriteUInt16BigEndian(_rksysData, mainOffset + 0x16, vr);
        BigEndianBinary.WriteUInt16BigEndian(_rksysData, mainOffset + 0x18, br);

        Array.Copy(serializedMii, 0, _rksysData, mainOffset + 0x1A, MiiSize);
        var miiCrc = Crc.ComputeCrc16Ccitt(serializedMii, 0, serializedMii.Length);
        BigEndianBinary.WriteUInt16BigEndian(_rksysData, mainOffset + 0x64, miiCrc);

        _rksysData[mainOffset + 0x66] = (byte)slotIndex;
        _rksysData[mainOffset + 0x67] = DefaultFriendGameRegion;
        _rksysData[mainOffset + 0x68] = DefaultFriendCountryCode;
        _rksysData[mainOffset + 0x69] = DefaultFriendRegionId;
        BigEndianBinary.WriteUInt16BigEndian(_rksysData, mainOffset + 0x6A, DefaultFriendCityId);
        BigEndianBinary.WriteUInt16BigEndian(_rksysData, mainOffset + 0x6C, 0);
        BigEndianBinary.WriteUInt16BigEndian(_rksysData, mainOffset + 0x6E, 0);

        for (var i = 0; i < 10; i++)
        {
            BigEndianBinary.WriteUInt32BigEndian(_rksysData, mainOffset + 0x70 + i * 8, 0xFFFFFFFF);
        }

        // One-sided pending friend request token.
        _rksysData[secondaryOffset + 0x2] = DwcFriendControlTypeFriendKey;
        BigEndianBinary.WriteUInt32BigEndian(_rksysData, secondaryOffset + 0x4, friendProfileId);
    }

    public OperationResult RemoveFriend(int userIndex, string friendCode)
    {
        if (userIndex is < 0 or >= MaxPlayerNum)
            return Fail("Invalid license index. Please select a valid license.");
        if (_rksysData is null || _rksysData.Length < RksysSize)
            return Fail("Invalid or unloaded rksys.dat data.");

        var normalizedFriendCode = NormalizeFriendCode(friendCode);
        if (normalizedFriendCode.IsFailure)
            return normalizedFriendCode.Error;

        var friendProfileId = FriendCode.FriendCodeToProfileId(normalizedFriendCode.Value);
        if (friendProfileId == 0)
            return Fail("Invalid friend code.");

        var rkpdOffset = RksysMagic.Length + userIndex * RkpdSize;
        var slotIndex = FindFriendSlotByProfileId(rkpdOffset, friendProfileId);
        if (slotIndex < 0)
            return Fail("Friend was not found in this license.");

        var mainOffset = rkpdOffset + FriendDataOffset + slotIndex * FriendDataSize;
        var secondaryOffset = rkpdOffset + FriendSecondaryDataOffset + slotIndex * FriendSecondaryDataSize;
        Array.Clear(_rksysData, mainOffset, FriendDataSize);
        Array.Clear(_rksysData, secondaryOffset, FriendSecondaryDataSize);

        var saveResult = SaveRksysToFile();
        if (saveResult.IsFailure)
            return saveResult.Error;

        return ParseUsers();
    }

    private int FindFriendSlotByProfileId(int rkpdOffset, uint friendProfileId)
    {
        if (_rksysData == null || friendProfileId == 0)
            return -1;

        var friendOffset = rkpdOffset + FriendDataOffset;
        for (var i = 0; i < MaxFriendNum; i++)
        {
            var currentOffset = friendOffset + i * FriendDataSize;
            var currentPid = BigEndianBinary.BufferToUint32(_rksysData, currentOffset + 0x04);
            if (currentPid == friendProfileId)
                return i;
        }

        return -1;
    }

    private static OperationResult<string> NormalizeFriendCode(string friendCode)
    {
        if (string.IsNullOrWhiteSpace(friendCode))
            return Fail("Friend code cannot be empty.");

        var digits = new string(friendCode.Where(char.IsDigit).ToArray());
        if (digits.Length != 12 || !ulong.TryParse(digits, out _))
            return Fail("Friend code must be exactly 12 digits.");

        var formatted = $"{digits[..4]}-{digits.Substring(4, 4)}-{digits.Substring(8, 4)}";
        var profileId = FriendCode.FriendCodeToProfileId(formatted);
        if (profileId == 0)
            return Fail("Invalid friend code.");

        return formatted;
    }

    private bool ValidateMagicNumber()
    {
        if (_rksysData == null)
            return false;
        return Encoding.ASCII.GetString(_rksysData, 0, RksysMagic.Length) == RksysMagic;
    }

    private OperationResult<byte[]> ReadRksys()
    {
        try
        {
            if (!_fileSystem.Directory.Exists(_distributionPaths.SaveFolderPath))
                return Fail("Save folder not found");

            var currentRegion = _settingsManager.Get<MarioKartWiiEnums.Regions>(_settingsManager.RR_REGION);
            if (currentRegion == MarioKartWiiEnums.Regions.None)
            {
                // Double check if there's at least one valid region
                var validRegions = _saveRegions.GetAvailableRegions(_distributionPaths.SaveFolderPath);
                if (validRegions.First() != MarioKartWiiEnums.Regions.None)
                {
                    currentRegion = validRegions.First();
                    _settingsManager.Set(_settingsManager.RR_REGION, currentRegion);
                }
                else
                {
                    return Fail("No valid regions found");
                }
            }

            var saveFileFolder = _fileSystem.Path.Combine(_distributionPaths.SaveFolderPath, GameRegion.GetGameId(currentRegion));
            var saveFile = _fileSystem.Directory.GetFiles(saveFileFolder, "rksys.dat", SearchOption.TopDirectoryOnly);
            if (saveFile.Length == 0)
                return Fail("rksys.dat not found");
            return _fileSystem.File.ReadAllBytes(saveFile[0]);
        }
        catch
        {
            return Fail("Failed to load rksys.dat");
        }
    }

    /// <summary>
    /// Fixes the MKWii save file by recalculating and inserting the CRC32 at 0x27FFC.
    /// </summary>
    public static void FixRksysCrc(byte[] rksysData)
    {
        if (rksysData == null || rksysData.Length < RksysSize)
            throw new ArgumentException("Invalid rksys.dat data");

        var lengthToCrc = 0x27FFC;
        var newCrc = Crc.ComputeCrc32(rksysData, 0, lengthToCrc);

        // 2) Write CRC at offset 0x27FFC in big-endian.
        BigEndianBinary.WriteUInt32BigEndian(rksysData, 0x27FFC, newCrc);
    }

    public OperationResult ChangeMiiName(int userIndex, string? newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            return Fail("Cannot set name to an empty name.");
        if (userIndex is < 0 or >= MaxPlayerNum)
            return Fail("Invalid license index. Please select a valid license.");

        var user = Licenses.Users[userIndex];
        var miiIsEmptyOrNoName = IsNoNameOrEmptyMii(user);

        if (miiIsEmptyOrNoName)
            return Fail("This license has no Mii data or is incomplete.\n" + "Please use the Mii Channel to create a Mii first.");

        if (user.Mii == null)
            return Fail("This license has no Mii data or is incomplete.\n" + "Please use the Mii Channel to create a Mii first.");

        newName = Regex.Replace(newName, @"\s+", " ");

        // Basic checks
        if (newName.Length is > 10 or < 1)
            return Fail("Names must be between 1 and 10 characters long.");

        if (newName.Length > 10)
            newName = newName.Substring(0, 10);
        var nameResult = MiiName.Create(newName);
        if (nameResult.IsFailure)
            return nameResult.Error;

        user.Mii.Name = nameResult.Value;
        var nameWrite = WriteLicenseNameToSaveData(userIndex, newName);
        if (nameWrite.IsFailure)
            return nameWrite.Error;
        var updated = _miiService.UpdateName(user.Mii.MiiId, newName);
        if (updated.IsFailure)
            return updated.Error;
        var rksysSaveResult = SaveRksysToFile();
        if (rksysSaveResult.IsFailure)
            return rksysSaveResult.Error;

        return Ok();
    }

    private bool IsNoNameOrEmptyMii(LicenseProfile user)
    {
        if (user?.Mii == null)
            return true;

        var name = user.Mii.Name;
        if (name.ToString() == "no name")
            return true;
        var raw = MiiSerializer.Serialize(user.Mii).Value;
        if (raw.Length != 74)
            return true; // Not valid
        if (raw.All(b => b == 0))
            return true;

        // Otherwise, it’s presumably valid
        return false;
    }

    private OperationResult WriteLicenseNameToSaveData(int userIndex, string newName)
    {
        if (_rksysData == null || _rksysData.Length < RksysSize)
            return Fail("Invalid save data");
        var rkpdOffset = 0x8 + userIndex * RkpdSize;
        var nameOffset = rkpdOffset + 0x14;
        var nameBytes = Encoding.BigEndianUnicode.GetBytes(newName);
        for (var i = 0; i < 20; i++)
            _rksysData[nameOffset + i] = 0;
        Array.Copy(nameBytes, 0, _rksysData, nameOffset, Math.Min(nameBytes.Length, 20));
        return Ok();
    }

    public IReadOnlyList<LicenseIdentity> GetLicenseIdentities()
    {
        var result = new List<LicenseIdentity>();
        if (_rksysData is null || _rksysData.Length < RksysSize)
            return result;

        for (var slot = 0; slot < MaxPlayerNum; slot++)
        {
            var offset = RksysMagic.Length + slot * RkpdSize;
            var profileId = BigEndianBinary.BufferToUint32(_rksysData, offset + 0x5C);
            if (profileId == 0)
                continue;

            var name = slot < Licenses.Users.Count ? Licenses.Users[slot].Mii?.Name.ToString() : null;
            result.Add(
                new LicenseIdentity(
                    slot,
                    string.IsNullOrWhiteSpace(name) ? "No name" : name,
                    profileId,
                    FriendCode.GetFriendCode(_rksysData, offset + 0x5C),
                    BigEndianBinary.BufferToUint32(_rksysData, offset + 0x4C),
                    BigEndianBinary.BufferToUint32(_rksysData, offset + 0x58)
                )
            );
        }

        return result;
    }

    public OperationResult ChangeProfileId(int userIndex, uint newProfileId)
    {
        if (userIndex is < 0 or >= MaxPlayerNum)
            return Fail("Invalid license index. Please select a valid license.");
        if (_rksysData is null || _rksysData.Length != RksysSize)
            return Fail("Invalid or unloaded rksys.dat data.");
        if (newProfileId == 0)
            return Fail("That is not a valid profile id.");

        var identities = GetLicenseIdentities();
        var others = identities.Where(l => l.Slot != userIndex).ToList();
        if (others.Any(l => l.ProfileId == newProfileId))
            return Fail("Another license on this save already uses that profile id.");

        var offset = RksysMagic.Length + userIndex * RkpdSize;
        var pseudo = BigEndianBinary.BufferToUint32(_rksysData, offset + 0x4C);
        var authentic = BigEndianBinary.BufferToUint32(_rksysData, offset + 0x58);

        // Raise the shared pseudo/authentic id to a value that no other license has.
        var next = LicenseIds.NextFree(pseudo, authentic, others);

        var backup = BackupRksys("change");
        if (backup.IsFailure)
            return backup;

        var oldProfileId = BigEndianBinary.BufferToUint32(_rksysData, offset + 0x5C);
        BigEndianBinary.WriteUInt32BigEndian(_rksysData, offset + 0x5C, newProfileId);
        BigEndianBinary.WriteUInt32BigEndian(_rksysData, offset + 0x4C, next);
        BigEndianBinary.WriteUInt32BigEndian(_rksysData, offset + 0x58, next);

        var saved = SaveRksysToFile();
        if (saved.IsFailure)
            return saved;

        MirrorToOtherRegions(oldProfileId, newProfileId, next, next, "change");
        return LoadLicense();
    }

    public OperationResult MakeIdsUnique(int userIndex)
    {
        if (userIndex is < 0 or >= MaxPlayerNum)
            return Fail("Invalid license index. Please select a valid license.");
        if (_rksysData is null || _rksysData.Length != RksysSize)
            return Fail("Invalid or unloaded rksys.dat data.");

        var identities = GetLicenseIdentities();
        if (identities.FirstOrDefault(l => l.Slot == userIndex) is not { } license)
            return Fail("That slot has no license.");

        var problems = LicenseIds.Check(license, identities);
        if (!problems.Any)
            return Fail("This license's ids are already fine.");
        if (problems.SameFriendCode(license))
            return Fail("Another license has the same friend code. Give one of them a new friend code instead.");

        var next = LicenseIds.NextFree(license.PseudoId, license.AuthenticId, identities.Where(l => l.Slot != userIndex));
        var backup = BackupRksys("change");
        if (backup.IsFailure)
            return backup;

        var offset = RksysMagic.Length + userIndex * RkpdSize;
        BigEndianBinary.WriteUInt32BigEndian(_rksysData, offset + 0x4C, next);
        BigEndianBinary.WriteUInt32BigEndian(_rksysData, offset + 0x58, next);
        var saved = SaveRksysToFile();
        if (saved.IsFailure)
            return saved;

        MirrorToOtherRegions(license.ProfileId, license.ProfileId, next, next, "change");
        return LoadLicense();
    }

    private static readonly MarioKartWiiEnums.Regions[] SaveRegionList =
    [
        MarioKartWiiEnums.Regions.America,
        MarioKartWiiEnums.Regions.Europe,
        MarioKartWiiEnums.Regions.Japan,
        MarioKartWiiEnums.Regions.Korea,
    ];

    /// <summary>The save folders of every other region that has a save, so one friend code can be kept on all of them.</summary>
    private IEnumerable<string> OtherRegionFolders()
    {
        var current = _settingsManager.Get<MarioKartWiiEnums.Regions>(_settingsManager.RR_REGION);
        foreach (var region in SaveRegionList.Where(r => r != current))
        {
            var folder = _fileSystem.Path.Combine(_distributionPaths.SaveFolderPath, GameRegion.GetGameId(region));
            if (_fileSystem.File.Exists(_fileSystem.Path.Combine(folder, "rksys.dat")))
                yield return folder;
        }
    }

    /// <summary>
    /// Gives the same license on the other regions' saves the same ids, so its friend code is the same whichever disc is
    /// played. "The same license" is the one that has <paramref name="matchProfileId"/> there now (in whatever slot): a
    /// different license in that slot is left alone, and so is a save where the new code is already taken. Each of those
    /// saves is backed up first. Best effort: the save being edited is already done by then.
    /// </summary>
    private void MirrorToOtherRegions(uint matchProfileId, uint profileId, uint pseudoId, uint authenticId, string kind)
    {
        foreach (var folder in OtherRegionFolders())
        {
            try
            {
                var path = _fileSystem.Path.Combine(folder, "rksys.dat");
                var data = _fileSystem.File.ReadAllBytes(path);
                if (ValidateSave(data).IsFailure)
                    continue;

                var slots = Enumerable
                    .Range(0, MaxPlayerNum)
                    .Select(slot => (Slot: slot, Id: BigEndianBinary.BufferToUint32(data, RksysMagic.Length + slot * RkpdSize + 0x5C)))
                    .ToList();
                var match = slots.FirstOrDefault(item => item.Id == matchProfileId && item.Id != 0);
                if (match.Id == 0 || slots.Any(item => item.Slot != match.Slot && item.Id == profileId))
                    continue;

                var offset = RksysMagic.Length + match.Slot * RkpdSize;
                CopyFolderBackup(folder, kind);
                BigEndianBinary.WriteUInt32BigEndian(data, offset + 0x5C, profileId);
                BigEndianBinary.WriteUInt32BigEndian(data, offset + 0x4C, pseudoId);
                BigEndianBinary.WriteUInt32BigEndian(data, offset + 0x58, authenticId);
                FixRksysCrc(data);
                _fileSystem.WriteAllBytesAtomic(path, data, "Failed to update another region's rksys.dat.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// A backup folder name stamped with the time. Backups are ordered by name, so the stamp always comes after the newest
    /// one already there (of any kind): a backup made in the same second as another still sorts after it.
    /// </summary>
    private string NewBackupName(string folder, string kind)
    {
        var root = _fileSystem.Path.Combine(folder, "backup");
        var stamp = DateTime.Now;
        if (_fileSystem.Directory.Exists(root))
        {
            foreach (var directory in _fileSystem.Directory.GetDirectories(root))
            {
                var name = _fileSystem.Path.GetFileName(directory);
                if (
                    name.Length >= 15
                    && DateTime.TryParseExact(
                        name[..15],
                        "yyyyMMdd-HHmmss",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None,
                        out var taken
                    )
                    && taken >= stamp.AddTicks(-(stamp.Ticks % TimeSpan.TicksPerSecond))
                )
                    stamp = taken.AddSeconds(1);
            }
        }

        return $"{stamp:yyyyMMdd-HHmmss}-{kind}";
    }

    private void CopyFolderBackup(string folder, string kind)
    {
        var name = NewBackupName(folder, kind);
        var directory = _fileSystem.Path.Combine(folder, "backup", name);
        _fileSystem.Directory.CreateDirectory(directory);
        _fileSystem.File.Copy(
            _fileSystem.Path.Combine(folder, "rksys.dat"),
            _fileSystem.Path.Combine(directory, "rksys.dat"),
            overwrite: true
        );
        PruneFolder(folder, KeepBackups);
    }

    /// <summary>Undo for the other regions: puts back the ids from each one's newest "change" backup.</summary>
    private void UndoOtherRegions()
    {
        foreach (var folder in OtherRegionFolders())
        {
            try
            {
                var root = _fileSystem.Path.Combine(folder, "backup");
                if (!_fileSystem.Directory.Exists(root))
                    continue;

                var latest = _fileSystem
                    .Directory.GetDirectories(root)
                    .Where(d => _fileSystem.Path.GetFileName(d).EndsWith("-change", StringComparison.Ordinal))
                    .OrderByDescending(d => _fileSystem.Path.GetFileName(d), StringComparer.Ordinal)
                    .FirstOrDefault();
                if (latest is null)
                    continue;

                var backup = _fileSystem.File.ReadAllBytes(_fileSystem.Path.Combine(latest, "rksys.dat"));
                var path = _fileSystem.Path.Combine(folder, "rksys.dat");
                var data = _fileSystem.File.ReadAllBytes(path);
                if (ValidateSave(backup).IsFailure || ValidateSave(data).IsFailure)
                    continue;

                var changed = false;
                for (var slot = 0; slot < MaxPlayerNum; slot++)
                {
                    var offset = RksysMagic.Length + slot * RkpdSize;
                    // Same as on the main save: leave licenses added or removed since alone.
                    if (
                        BigEndianBinary.BufferToUint32(backup, offset + 0x5C) == 0
                        || BigEndianBinary.BufferToUint32(data, offset + 0x5C) == 0
                    )
                        continue;

                    foreach (var field in new[] { 0x4C, 0x58, 0x5C })
                    {
                        var old = BigEndianBinary.BufferToUint32(backup, offset + field);
                        if (old == BigEndianBinary.BufferToUint32(data, offset + field))
                            continue;
                        BigEndianBinary.WriteUInt32BigEndian(data, offset + field, old);
                        changed = true;
                    }
                }

                if (!changed)
                    continue;

                CopyFolderBackup(folder, "undo");
                FixRksysCrc(data);
                _fileSystem.WriteAllBytesAtomic(path, data, "Failed to update another region's rksys.dat.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private string SaveFolderForRegion()
    {
        var region = _settingsManager.Get<MarioKartWiiEnums.Regions>(_settingsManager.RR_REGION);
        return _fileSystem.Path.Combine(_distributionPaths.SaveFolderPath, GameRegion.GetGameId(region));
    }

    /// <summary>Every backup is its own folder, named by time and kind, holding a plain rksys.dat.</summary>
    public IReadOnlyList<SaveBackup> ListBackups()
    {
        var result = new List<SaveBackup>();
        try
        {
            var root = _fileSystem.Path.Combine(SaveFolderForRegion(), "backup");
            if (!_fileSystem.Directory.Exists(root))
                return result;

            foreach (var directory in _fileSystem.Directory.GetDirectories(root))
            {
                var file = _fileSystem.Path.Combine(directory, "rksys.dat");
                if (!_fileSystem.File.Exists(file))
                    continue;

                var name = _fileSystem.Path.GetFileName(directory);
                var kind = name.Length > 16 ? name[16..] : "manual";
                var time =
                    name.Length >= 15
                    && DateTime.TryParseExact(
                        name[..15],
                        "yyyyMMdd-HHmmss",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None,
                        out var parsed
                    )
                        ? parsed
                        : _fileSystem.File.GetLastWriteTime(file);
                result.Add(new SaveBackup(name, kind, time, file, ReadLabel(directory)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        // The stamp sorts by time, so the biggest name is the newest.
        return result.OrderByDescending(b => b.Name, StringComparer.Ordinal).ToList();
    }

    // How many ordinary backups each save folder keeps; older ones are removed whenever a new one is made.
    private const int KeepBackups = 30;
    private const string LabelFile = "name.txt";

    private string? ReadLabel(string directory)
    {
        try
        {
            var file = _fileSystem.Path.Combine(directory, LabelFile);
            return _fileSystem.File.Exists(file) && _fileSystem.File.ReadAllText(file).Trim() is { Length: > 0 } label ? label : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public OperationResult NameBackup(string name, string label)
    {
        if (ListBackups().FirstOrDefault(b => b.Name == name) is not { } backup)
            return Fail("That backup was not found.");

        try
        {
            var file = _fileSystem.Path.Combine(_fileSystem.Path.GetDirectoryName(backup.FilePath)!, LabelFile);
            label = label.Trim();
            if (label.Length == 0)
            {
                if (_fileSystem.File.Exists(file))
                    _fileSystem.File.Delete(file);
            }
            else
                _fileSystem.File.WriteAllText(file, label);
            return Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail("Could not name that backup.");
        }
    }

    public OperationResult DeleteBackup(string name)
    {
        if (ListBackups().FirstOrDefault(b => b.Name == name) is not { } backup)
            return Fail("That backup was not found.");

        try
        {
            _fileSystem.Directory.Delete(_fileSystem.Path.GetDirectoryName(backup.FilePath)!, recursive: true);
            return Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail("Could not delete that backup.");
        }
    }

    public int PruneBackups(int keep) => PruneFolder(SaveFolderForRegion(), keep);

    private int PruneFolder(string folder, int keep)
    {
        var removed = 0;
        try
        {
            var root = _fileSystem.Path.Combine(folder, "backup");
            if (!_fileSystem.Directory.Exists(root))
                return 0;

            var old = _fileSystem
                .Directory.GetDirectories(root)
                .Where(directory =>
                {
                    var name = _fileSystem.Path.GetFileName(directory);
                    return _fileSystem.File.Exists(_fileSystem.Path.Combine(directory, "rksys.dat"))
                        && !_fileSystem.File.Exists(_fileSystem.Path.Combine(directory, LabelFile))
                        && !name.EndsWith("-change", StringComparison.Ordinal)
                        && !name.EndsWith($"-{RestoreKind}", StringComparison.Ordinal);
                })
                .OrderByDescending(directory => _fileSystem.Path.GetFileName(directory), StringComparer.Ordinal)
                .Skip(keep)
                .ToList();
            foreach (var directory in old)
            {
                try
                {
                    _fileSystem.Directory.Delete(directory, recursive: true);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        return removed;
    }

    public IReadOnlyList<BackupLicense> GetBackupLicenses(string name)
    {
        return ListBackups().FirstOrDefault(b => b.Name == name) is { } backup ? ReadSaveLicenses(backup.FilePath) : [];
    }

    // A restore from the history is undone the same way as a code change.
    private SaveBackup? NewestChangeBackup() => ListBackups().FirstOrDefault(b => b.Kind is "change" or RestoreKind);

    private const string RestoreKind = "restore-change";

    public OperationResult RestoreFriendCode(string backupName, int slot)
    {
        if (slot is < 0 or >= MaxPlayerNum)
            return Fail("Invalid license index. Please select a valid license.");
        if (_rksysData is null || _rksysData.Length != RksysSize)
            return Fail("Invalid or unloaded rksys.dat data.");
        if (ListBackups().FirstOrDefault(b => b.Name == backupName) is not { } backup)
            return Fail("That backup was not found.");

        byte[] data;
        try
        {
            data = _fileSystem.File.ReadAllBytes(backup.FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail("Could not read the backup.");
        }

        var valid = ValidateSave(data);
        if (valid.IsFailure)
            return valid;

        var offset = RksysMagic.Length + slot * RkpdSize;
        var profileId = BigEndianBinary.BufferToUint32(data, offset + 0x5C);
        var pseudoId = BigEndianBinary.BufferToUint32(data, offset + 0x4C);
        var authenticId = BigEndianBinary.BufferToUint32(data, offset + 0x58);
        if (profileId == 0)
            return Fail("That license did not exist then.");
        if (BigEndianBinary.BufferToUint32(_rksysData, offset + 0x5C) == 0)
            return Fail("That slot has no license now.");
        if (
            BigEndianBinary.BufferToUint32(_rksysData, offset + 0x5C) == profileId
            && BigEndianBinary.BufferToUint32(_rksysData, offset + 0x4C) == pseudoId
            && BigEndianBinary.BufferToUint32(_rksysData, offset + 0x58) == authenticId
        )
            return Fail("This license already has that friend code.");
        if (GetLicenseIdentities().Any(l => l.Slot != slot && l.ProfileId == profileId))
            return Fail("Another license on this save already uses that friend code.");

        var safety = BackupRksys(RestoreKind);
        if (safety.IsFailure)
            return safety;

        var currentProfileId = BigEndianBinary.BufferToUint32(_rksysData, offset + 0x5C);
        BigEndianBinary.WriteUInt32BigEndian(_rksysData, offset + 0x5C, profileId);
        BigEndianBinary.WriteUInt32BigEndian(_rksysData, offset + 0x4C, pseudoId);
        BigEndianBinary.WriteUInt32BigEndian(_rksysData, offset + 0x58, authenticId);
        var saved = SaveRksysToFile();
        if (saved.IsFailure)
            return saved;

        MirrorToOtherRegions(currentProfileId, profileId, pseudoId, authenticId, RestoreKind);
        return LoadLicense();
    }

    public OperationResult<string> BackupSaveNow()
    {
        var result = BackupRksys("manual", out var name);
        if (result.IsFailure)
            return result.Error;

        return Ok(name);
    }

    public DateTime? GetLastBackupTime() => NewestChangeBackup()?.Time;

    public IReadOnlyList<FriendCodeChange> GetFriendCodeHistory()
    {
        var history = new List<FriendCodeChange>();
        if (_rksysData is null || _rksysData.Length != RksysSize)
            return history;

        // Each backup is the save just before something changed it, so comparing it with the next backup (or with the save
        // as it is now) shows what that change did. Manual backups change nothing and drop out on their own.
        var backups = ListBackups().Reverse().ToList();
        var states = backups.Select(b => ReadLicenseSlots(b.FilePath)).ToList();
        states.Add(LicenseSlots(_rksysData));
        for (var i = 0; i < backups.Count; i++)
        {
            var before = states[i];
            if (before is null)
                continue;

            // An unreadable backup is skipped over, so the change is compared with the next state that can be read.
            var after = states.Skip(i + 1).FirstOrDefault(state => state is not null);
            if (after is null)
                continue;

            for (var slot = 0; slot < MaxPlayerNum; slot++)
            {
                if (before[slot].Code == after[slot].Code)
                    continue;

                var name = after[slot].Code.Length > 0 ? after[slot].Name : before[slot].Name;
                history.Add(new(backups[i].Time, backups[i].Kind, slot, name, before[slot].Code, after[slot].Code, backups[i].Name));
            }
        }

        history.Reverse();
        return history;
    }

    private const int LicenseAreaSize = 0x8 + MaxPlayerNum * RkpdSize;

    /// <summary>The name and friend code of each slot of a saved rksys.dat, or null when it is not a save.</summary>
    private (string Name, string Code, uint PseudoId, uint AuthenticId)[]? ReadLicenseSlots(string path)
    {
        try
        {
            using var stream = _fileSystem.File.OpenRead(path);
            if (stream.Length != RksysSize)
                return null;

            // Only the licenses at the start are needed, not the whole 2.8 MB.
            var data = new byte[LicenseAreaSize];
            stream.ReadExactly(data);
            return Encoding.ASCII.GetString(data, 0, RksysMagic.Length) == RksysMagic ? LicenseSlots(data) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static (string Name, string Code, uint PseudoId, uint AuthenticId)[] LicenseSlots(byte[] data)
    {
        var slots = new (string Name, string Code, uint PseudoId, uint AuthenticId)[MaxPlayerNum];
        for (var slot = 0; slot < MaxPlayerNum; slot++)
        {
            var offset = RksysMagic.Length + slot * RkpdSize;
            if (BigEndianBinary.BufferToUint32(data, offset + 0x5C) == 0)
            {
                slots[slot] = (string.Empty, string.Empty, 0, 0);
                continue;
            }

            // Mii names can hold the game's own symbols (private-use characters) that no font here can draw.
            var raw = Encoding.BigEndianUnicode.GetString(data, offset + 0x14, 20).TrimEnd('\0');
            var name = new string(raw.Where(c => c is < '' or > '').ToArray()).Trim();
            slots[slot] = (
                name,
                FriendCode.GetFriendCode(data, offset + 0x5C),
                BigEndianBinary.BufferToUint32(data, offset + 0x4C),
                BigEndianBinary.BufferToUint32(data, offset + 0x58)
            );
        }

        return slots;
    }

    private static OperationResult ValidateSave(byte[] data)
    {
        if (data.Length != RksysSize || Encoding.ASCII.GetString(data, 0, RksysMagic.Length) != RksysMagic)
            return Fail("That is not a Mario Kart Wii save file (rksys.dat).");
        return Ok();
    }

    public OperationResult RestoreBackup(string name)
    {
        if (ListBackups().FirstOrDefault(b => b.Name == name) is not { } backup)
            return Fail("That backup was not found.");

        byte[] data;
        try
        {
            data = _fileSystem.File.ReadAllBytes(backup.FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail("Could not read that backup.");
        }

        var valid = ValidateSave(data);
        if (valid.IsFailure)
            return valid;

        // Keep how it is now, in case this was a mistake.
        var safety = BackupRksys("before-restore");
        if (safety.IsFailure)
            return safety;

        var path = _fileSystem.Path.Combine(SaveFolderForRegion(), "rksys.dat");
        var written = _fileSystem.WriteAllBytesAtomic(path, data, "Failed to restore rksys.dat.");
        if (written.IsFailure)
            return written;

        return LoadLicense();
    }

    public OperationResult ExportSave(string destinationPath)
    {
        try
        {
            var path = _fileSystem.Path.Combine(SaveFolderForRegion(), "rksys.dat");
            if (!_fileSystem.File.Exists(path))
                return Fail("rksys.dat not found.");

            _fileSystem.File.Copy(path, destinationPath, overwrite: true);
            return Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail("Could not save the file there.");
        }
    }

    public OperationResult ImportSave(string sourcePath)
    {
        byte[] data;
        try
        {
            data = _fileSystem.File.ReadAllBytes(sourcePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail("Could not read that file.");
        }

        var valid = ValidateSave(data);
        if (valid.IsFailure)
            return valid;

        var safety = BackupRksys("before-import");
        if (safety.IsFailure)
            return safety;

        FixRksysCrc(data);
        var path = _fileSystem.Path.Combine(SaveFolderForRegion(), "rksys.dat");
        var written = _fileSystem.WriteAllBytesAtomic(path, data, "Failed to import rksys.dat.");
        if (written.IsFailure)
            return written;

        return LoadLicense();
    }

    public OperationResult ExportLicense(int userIndex, string destinationPath)
    {
        if (userIndex is < 0 or >= MaxPlayerNum)
            return Fail("Invalid license index. Please select a valid license.");
        if (_rksysData is null || _rksysData.Length != RksysSize)
            return Fail("Invalid or unloaded rksys.dat data.");

        var offset = RksysMagic.Length + userIndex * RkpdSize;
        if (BigEndianBinary.BufferToUint32(_rksysData, offset + 0x5C) == 0)
            return Fail("That slot has no license.");

        try
        {
            _fileSystem.File.WriteAllBytes(destinationPath, _rksysData.AsSpan(offset, RkpdSize).ToArray());
            return Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail("Could not save the file there.");
        }
    }

    public OperationResult<LicenseFile> ReadLicenseFile(string sourcePath)
    {
        var read = ReadRkp(sourcePath);
        if (read.IsFailure)
            return read.Error;

        var data = read.Value;
        var name = Encoding.BigEndianUnicode.GetString(data, 0x14, 20).TrimEnd('\0').Trim();
        return new LicenseFile(
            name,
            FriendCode.GetFriendCode(data, 0x5C),
            BigEndianBinary.BufferToUint32(data, 0x5C),
            BigEndianBinary.BufferToUint32(data, 0x4C),
            BigEndianBinary.BufferToUint32(data, 0x58)
        );
    }

    public OperationResult ImportLicense(int userIndex, string sourcePath)
    {
        if (userIndex is < 0 or >= MaxPlayerNum)
            return Fail("Invalid license index. Please select a valid license.");
        if (_rksysData is null || _rksysData.Length != RksysSize)
            return Fail("Invalid or unloaded rksys.dat data.");

        var read = ReadRkp(sourcePath);
        if (read.IsFailure)
            return read.Error;

        return PutLicense(userIndex, read.Value);
    }

    public IReadOnlyList<BackupLicense> ReadSaveLicenses(string savePath)
    {
        var slots = ReadLicenseSlots(savePath);
        var result = new List<BackupLicense>();
        if (slots is null)
            return result;

        for (var slot = 0; slot < MaxPlayerNum; slot++)
            if (slots[slot].Code.Length > 0)
                result.Add(new BackupLicense(slot, slots[slot].Name, slots[slot].Code, slots[slot].PseudoId, slots[slot].AuthenticId));
        return result;
    }

    public OperationResult ImportLicenseFromSave(int userIndex, string savePath, int sourceSlot)
    {
        if (userIndex is < 0 or >= MaxPlayerNum || sourceSlot is < 0 or >= MaxPlayerNum)
            return Fail("Invalid license index. Please select a valid license.");
        if (_rksysData is null || _rksysData.Length != RksysSize)
            return Fail("Invalid or unloaded rksys.dat data.");

        byte[] data;
        try
        {
            data = _fileSystem.File.ReadAllBytes(savePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail("Could not read that file.");
        }

        var valid = ValidateSave(data);
        if (valid.IsFailure)
            return valid;

        var offset = RksysMagic.Length + sourceSlot * RkpdSize;
        if (
            Encoding.ASCII.GetString(data, offset, RkpdMagic.Length) != RkpdMagic
            || BigEndianBinary.BufferToUint32(data, offset + 0x5C) == 0
        )
            return Fail("That license does not exist in that save.");

        return PutLicense(userIndex, data.AsSpan(offset, RkpdSize).ToArray());
    }

    public OperationResult ImportLicensesFromSave(string savePath, IReadOnlyList<int> slots)
    {
        if (slots.Count == 0 || slots.Any(slot => slot is < 0 or >= MaxPlayerNum))
            return Fail("Invalid license index. Please select a valid license.");
        if (_rksysData is null || _rksysData.Length != RksysSize)
            return Fail("Invalid or unloaded rksys.dat data.");

        byte[] data;
        try
        {
            data = _fileSystem.File.ReadAllBytes(savePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail("Could not read that file.");
        }

        var valid = ValidateSave(data);
        if (valid.IsFailure)
            return valid;

        var blocks = new List<(int Slot, byte[] Block)>();
        foreach (var slot in slots.Distinct())
        {
            var offset = RksysMagic.Length + slot * RkpdSize;
            if (
                Encoding.ASCII.GetString(data, offset, RkpdMagic.Length) != RkpdMagic
                || BigEndianBinary.BufferToUint32(data, offset + 0x5C) == 0
            )
                return Fail($"License {slot + 1} does not exist in that save.");
            blocks.Add((slot, data.AsSpan(offset, RkpdSize).ToArray()));
        }

        return PutLicenses(blocks);
    }

    public OperationResult ImportLicenseFiles(IReadOnlyList<(int Slot, string Path)> files)
    {
        if (
            files.Count == 0
            || files.Any(file => file.Slot is < 0 or >= MaxPlayerNum)
            || files.Select(file => file.Slot).Distinct().Count() != files.Count
        )
            return Fail("Invalid license index. Please select a valid license.");
        if (_rksysData is null || _rksysData.Length != RksysSize)
            return Fail("Invalid or unloaded rksys.dat data.");

        var blocks = new List<(int Slot, byte[] Block)>();
        foreach (var (slot, path) in files)
        {
            var read = ReadRkp(path);
            if (read.IsFailure)
                return read.Error;
            blocks.Add((slot, read.Value));
        }

        return PutLicenses(blocks);
    }

    /// <summary>Writes one license block into a slot, after a backup of the save, and reloads.</summary>
    private OperationResult PutLicense(int userIndex, byte[] block) => PutLicenses([(userIndex, block)]);

    private OperationResult PutLicenses(IReadOnlyList<(int Slot, byte[] Block)> blocks)
    {
        var backup = BackupRksys("before-license-import");
        if (backup.IsFailure)
            return backup;

        foreach (var (slot, block) in blocks)
            Array.Copy(block, 0, _rksysData!, RksysMagic.Length + slot * RkpdSize, RkpdSize);
        var saved = SaveRksysToFile();
        if (saved.IsFailure)
            return saved;

        return LoadLicense();
    }

    /// <summary>A .rkp is exactly one RKPD block; anything else would spill into the next license.</summary>
    private OperationResult<byte[]> ReadRkp(string sourcePath)
    {
        byte[] data;
        try
        {
            data = _fileSystem.File.ReadAllBytes(sourcePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail("Could not read that file.");
        }

        if (data.Length != RkpdSize || Encoding.ASCII.GetString(data, 0, RkpdMagic.Length) != RkpdMagic)
            return Fail("That is not a Mario Kart Wii license file (.rkp).");
        if (BigEndianBinary.BufferToUint32(data, 0x5C) == 0)
            return Fail("That license file is empty.");

        return data;
    }

    public OperationResult UndoLastProfileIdChange()
    {
        if (_rksysData is null || _rksysData.Length != RksysSize)
            return Fail("Invalid or unloaded rksys.dat data.");
        if (NewestChangeBackup() is not { } latest)
            return Fail("There is no backup to go back to.");

        byte[] backup;
        try
        {
            backup = _fileSystem.File.ReadAllBytes(latest.FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail("Could not read the backup.");
        }

        if (backup.Length != RksysSize)
            return Fail("The backup is not a valid save file.");

        var changed = new List<int>();
        for (var slot = 0; slot < MaxPlayerNum; slot++)
        {
            var offset = RksysMagic.Length + slot * RkpdSize;
            // A slot that was empty then (or is empty now) holds a license added or removed since, not a changed code:
            // putting the backup's zero ids on it would break that license.
            if (
                BigEndianBinary.BufferToUint32(backup, offset + 0x5C) == 0
                || BigEndianBinary.BufferToUint32(_rksysData, offset + 0x5C) == 0
            )
                continue;

            foreach (var field in new[] { 0x4C, 0x58, 0x5C })
            {
                if (BigEndianBinary.BufferToUint32(backup, offset + field) != BigEndianBinary.BufferToUint32(_rksysData, offset + field))
                {
                    changed.Add(slot);
                    break;
                }
            }
        }

        if (changed.Count == 0)
            return Fail("Nothing to undo: the friend codes already match the backup.");

        // Keep a copy of how it is now, in case the undo itself was a mistake.
        var safety = BackupRksys("undo");
        if (safety.IsFailure)
            return safety;

        foreach (var slot in changed)
        {
            var offset = RksysMagic.Length + slot * RkpdSize;
            foreach (var field in new[] { 0x4C, 0x58, 0x5C })
                BigEndianBinary.WriteUInt32BigEndian(_rksysData, offset + field, BigEndianBinary.BufferToUint32(backup, offset + field));
        }

        var saved = SaveRksysToFile();
        if (saved.IsFailure)
            return saved;

        UndoOtherRegions();
        return LoadLicense();
    }

    private OperationResult BackupRksys(string kind) => BackupRksys(kind, out _);

    /// <summary>Copies rksys.dat into its own dated folder inside "backup", before it is changed.</summary>
    private OperationResult BackupRksys(string kind, out string name)
    {
        name = string.Empty;
        try
        {
            var folder = SaveFolderForRegion();
            var path = _fileSystem.Path.Combine(folder, "rksys.dat");
            if (!_fileSystem.File.Exists(path))
                return Fail("rksys.dat not found.");

            // Two backups in the same second must not share a folder, or the later one would overwrite the earlier state.
            name = NewBackupName(folder, kind);
            var directory = _fileSystem.Path.Combine(folder, "backup", name);
            _fileSystem.Directory.CreateDirectory(directory);
            _fileSystem.File.Copy(path, _fileSystem.Path.Combine(directory, "rksys.dat"), overwrite: true);
            PruneFolder(folder, KeepBackups);
            return Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail("Could not make a backup of rksys.dat, so nothing was changed.");
        }
    }

    private OperationResult SaveRksysToFile()
    {
        if (_rksysData == null || !_settingsManager.PathsSetupCorrectly())
            return Fail("Invalid save data or config is not setup properly.");

        // Never write a save file with a wrong size, that would corrupt every license on it.
        if (_rksysData.Length != RksysSize)
            return Fail($"Refusing to save rksys.dat: expected {RksysSize} bytes but got {_rksysData.Length}.");

        FixRksysCrc(_rksysData);
        var currentRegion = _settingsManager.Get<MarioKartWiiEnums.Regions>(_settingsManager.RR_REGION);
        var saveFolder = _fileSystem.Path.Combine(_distributionPaths.SaveFolderPath, GameRegion.GetGameId(currentRegion));
        var path = _fileSystem.Path.Combine(saveFolder, "rksys.dat");
        return _fileSystem.WriteAllBytesAtomic(path, _rksysData, "Failed to save rksys.dat.");
    }

    protected override Task ExecuteTaskAsync(CancellationToken cancellationToken)
    {
        var result = LoadLicense();
        if (result.IsFailure)
        {
            throw new(result.Error.Message);
        }

        return Task.CompletedTask;
    }
}
