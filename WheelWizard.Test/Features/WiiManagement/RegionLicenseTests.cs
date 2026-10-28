using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Testably.Abstractions.Testing;
using WheelWizard.CustomDistributions;
using WheelWizard.Dolphin.Paths;
using WheelWizard.Models.Enums;
using WheelWizard.RrRooms;
using WheelWizard.Settings;
using WheelWizard.Settings.Types;
using WheelWizard.Shared.Binary;
using WheelWizard.Shared.Polling;
using WheelWizard.WheelWizardData;
using WheelWizard.WiiManagement.GameLicense;
using WheelWizard.WiiManagement.MiiManagement;

namespace WheelWizard.Test.Features.WiiManagement;

public class RegionLicenseTests
{
    [Fact]
    public void RegionSummaries_KeepSparseSlotIndexes_AndDoNotChangeActiveData()
    {
        var (service, fs, settings) = Create();
        AddSave(fs, MarioKartWiiEnums.Regions.Europe, 0, 3);
        AddSave(fs, MarioKartWiiEnums.Regions.Japan, 2);
        var users = service.LicenseCollection.Users;
        var profiles = service.GetRegionLicenses();
        Assert.Equal(3, profiles.Count);
        Assert.Equal(new[] { 0, 3 }, profiles.Where(p => p.Region == MarioKartWiiEnums.Regions.Europe).Select(p => p.Slot));
        Assert.Equal("Slot 3", profiles.Single(p => p.Region == MarioKartWiiEnums.Regions.Japan).Name);
        Assert.Same(users, service.LicenseCollection.Users);
        Assert.Empty(users);
        Assert.DoesNotContain(settings.ReceivedCalls(), call => call.GetMethodInfo().Name == "Set");
    }

    [Fact]
    public void RegionSummaries_IncludeAtMostFourProfilesPerRegion()
    {
        var (service, fs, _) = Create();
        foreach (var region in Enum.GetValues<MarioKartWiiEnums.Regions>().Where(r => r != MarioKartWiiEnums.Regions.None))
            AddSave(fs, region, 0, 1, 2, 3);
        Assert.Equal(16, service.GetRegionLicenses().Count);
    }

    [Fact]
    public void RegionSummaries_IgnoreTruncatedAndEmptySaves()
    {
        var (service, fs, _) = Create();
        AddSave(fs, MarioKartWiiEnums.Regions.Europe);
        fs.Directory.CreateDirectory("/saves/RMCJ");
        fs.File.WriteAllText("/saves/RMCJ/rksys.dat", "RKSD0006");
        Assert.Empty(service.GetRegionLicenses());
    }

    [Fact]
    public void ChangeProfileId_RenewsTheIds_MakesABackup_AndLeavesOtherLicensesAlone()
    {
        var (service, fs, settings) = Create();
        settings.Get<MarioKartWiiEnums.Regions>(Arg.Any<Setting<MarioKartWiiEnums.Regions>>()).Returns(MarioKartWiiEnums.Regions.Europe);
        settings.PathsSetupCorrectly().Returns(true);
        AddSave(fs, MarioKartWiiEnums.Regions.Europe, 0, 1);
        Assert.True(service.LoadLicense().IsSuccess);

        var result = service.ChangeProfileId(0, 200_000);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        var ids = service.GetLicenseIdentities();
        var changed = ids.Single(i => i.Slot == 0);
        var other = ids.Single(i => i.Slot == 1);
        Assert.Equal(200_000u, changed.ProfileId);
        Assert.Equal(changed.PseudoId, changed.AuthenticId);
        Assert.NotEqual(other.PseudoId, changed.PseudoId);
        Assert.NotEqual(100u, changed.PseudoId);
        Assert.Equal(2u, other.ProfileId);
        Assert.Equal(101u, other.PseudoId);
        var backup = Assert.Single(fs.Directory.GetDirectories("/saves/RMCP/backup"));
        Assert.EndsWith("-change", backup);
        Assert.True(fs.File.Exists(fs.Path.Combine(backup, "rksys.dat")));
    }

    [Fact]
    public void ChangeProfileId_AlsoPutsTheSameCodeOnTheOtherRegionsSaves_AndUndoRevertsThem()
    {
        var (service, fs, settings) = Create();
        settings.Get<MarioKartWiiEnums.Regions>(Arg.Any<Setting<MarioKartWiiEnums.Regions>>()).Returns(MarioKartWiiEnums.Regions.Europe);
        settings.PathsSetupCorrectly().Returns(true);
        AddSave(fs, MarioKartWiiEnums.Regions.Europe, 0);
        AddSave(fs, MarioKartWiiEnums.Regions.America, 0);
        Assert.True(service.LoadLicense().IsSuccess);
        var americaBefore = fs.File.ReadAllBytes("/saves/RMCE/rksys.dat");

        Assert.True(service.ChangeProfileId(0, 200_000).IsSuccess);

        var america = fs.File.ReadAllBytes("/saves/RMCE/rksys.dat");
        Assert.Equal(200_000u, BigEndianBinary.BufferToUint32(america, 8 + 0x5C));
        Assert.Single(fs.Directory.GetDirectories("/saves/RMCE/backup", "*-change"));

        Assert.True(service.UndoLastProfileIdChange().IsSuccess);

        var americaAfter = fs.File.ReadAllBytes("/saves/RMCE/rksys.dat");
        Assert.Equal(BigEndianBinary.BufferToUint32(americaBefore, 8 + 0x5C), BigEndianBinary.BufferToUint32(americaAfter, 8 + 0x5C));
    }

    [Fact]
    public void UndoLastProfileIdChange_PutsTheIdsBack_AndKeepsOtherProgress()
    {
        var (service, fs, settings) = Create();
        settings.Get<MarioKartWiiEnums.Regions>(Arg.Any<Setting<MarioKartWiiEnums.Regions>>()).Returns(MarioKartWiiEnums.Regions.Europe);
        settings.PathsSetupCorrectly().Returns(true);
        AddSave(fs, MarioKartWiiEnums.Regions.Europe, 0, 1);
        Assert.True(service.LoadLicense().IsSuccess);
        var original = service.GetLicenseIdentities();
        Assert.True(service.ChangeProfileId(0, 200_000).IsSuccess);
        Assert.NotNull(service.GetLastBackupTime());

        var undone = service.UndoLastProfileIdChange();

        Assert.True(undone.IsSuccess, undone.IsFailure ? undone.Error.Message : null);
        Assert.Equal(original, service.GetLicenseIdentities());
        // The undo kept a copy of the changed state, and a second undo has nothing left to do.
        Assert.Single(fs.Directory.GetDirectories("/saves/RMCP/backup", "*-undo"));
        Assert.True(service.UndoLastProfileIdChange().IsFailure);
    }

    [Fact]
    public void GetSaveWriteTimeUtc_IsNullWithoutASave_AndTheFileTimeWithOne()
    {
        var (service, fs, settings) = Create();
        settings.Get<MarioKartWiiEnums.Regions>(Arg.Any<Setting<MarioKartWiiEnums.Regions>>()).Returns(MarioKartWiiEnums.Regions.Europe);
        Assert.Null(service.GetSaveWriteTimeUtc());

        AddSave(fs, MarioKartWiiEnums.Regions.Europe, 0);

        var written = service.GetSaveWriteTimeUtc();
        Assert.NotNull(written);
        Assert.Equal(fs.File.GetLastWriteTimeUtc("/saves/RMCP/rksys.dat"), written);
    }

    [Fact]
    public void BackupSaveNow_CopiesTheSave_WithoutBecomingTheUndoPoint()
    {
        var (service, fs, settings) = Create();
        settings.Get<MarioKartWiiEnums.Regions>(Arg.Any<Setting<MarioKartWiiEnums.Regions>>()).Returns(MarioKartWiiEnums.Regions.Europe);
        AddSave(fs, MarioKartWiiEnums.Regions.Europe, 0);
        service.LoadLicense();

        var result = service.BackupSaveNow();

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        Assert.Single(fs.Directory.GetDirectories("/saves/RMCP/backup", "*-manual"));
        Assert.Null(service.GetLastBackupTime());
    }

    [Fact]
    public void BackupSaveNow_TwiceInOneSecond_KeepsBothBackups()
    {
        var (service, fs, settings) = Create();
        settings.Get<MarioKartWiiEnums.Regions>(Arg.Any<Setting<MarioKartWiiEnums.Regions>>()).Returns(MarioKartWiiEnums.Regions.Europe);
        AddSave(fs, MarioKartWiiEnums.Regions.Europe, 0);
        service.LoadLicense();

        var first = service.BackupSaveNow();
        var second = service.BackupSaveNow();

        Assert.True(first.IsSuccess && second.IsSuccess);
        Assert.Equal(2, fs.Directory.GetDirectories("/saves/RMCP/backup", "*-manual").Length);
    }

    [Fact]
    public void RestoreExportAndImport_RoundTripTheWholeSave()
    {
        var (service, fs, settings) = Create();
        settings.Get<MarioKartWiiEnums.Regions>(Arg.Any<Setting<MarioKartWiiEnums.Regions>>()).Returns(MarioKartWiiEnums.Regions.Europe);
        settings.PathsSetupCorrectly().Returns(true);
        AddSave(fs, MarioKartWiiEnums.Regions.Europe, 0, 1);
        service.LoadLicense();
        var original = fs.File.ReadAllBytes("/saves/RMCP/rksys.dat");

        fs.Directory.CreateDirectory("/export");
        // Export writes a plain copy, import brings it back and backs up what was there.
        Assert.True(service.ExportSave("/export/rksys.dat").IsSuccess);
        Assert.Equal(original, fs.File.ReadAllBytes("/export/rksys.dat"));
        Assert.True(service.ChangeProfileId(0, 200_000).IsSuccess);
        Assert.NotEqual(original, fs.File.ReadAllBytes("/saves/RMCP/rksys.dat"));
        Assert.True(service.ImportSave("/export/rksys.dat").IsSuccess);
        Assert.Equal(1u, service.GetLicenseIdentities().Single(i => i.Slot == 0).ProfileId);
        Assert.Single(fs.Directory.GetDirectories("/saves/RMCP/backup", "*-before-import"));

        // Restore puts a chosen backup back, whole.
        var change = service.ListBackups().Single(b => b.Kind == "change");
        Assert.True(service.RestoreBackup(change.Name).IsSuccess);
        Assert.Equal(1u, service.GetLicenseIdentities().Single(i => i.Slot == 0).ProfileId);
        Assert.Single(fs.Directory.GetDirectories("/saves/RMCP/backup", "*-before-restore"));
    }

    [Fact]
    public void GetBackupLicenses_ShowsTheFriendCodesInsideABackup()
    {
        var (service, fs, settings) = Create();
        settings.Get<MarioKartWiiEnums.Regions>(Arg.Any<Setting<MarioKartWiiEnums.Regions>>()).Returns(MarioKartWiiEnums.Regions.Europe);
        settings.PathsSetupCorrectly().Returns(true);
        AddSave(fs, MarioKartWiiEnums.Regions.Europe, 0, 1);
        service.LoadLicense();
        var before = service.GetLicenseIdentities();
        Assert.True(service.ChangeProfileId(0, 200_000).IsSuccess);

        var backup = service.ListBackups().Single(b => b.Kind == "change");
        var licenses = service.GetBackupLicenses(backup.Name);

        Assert.Equal(2, licenses.Count);
        Assert.Equal(before.Select(i => i.FriendCode), licenses.Select(l => l.FriendCode));
        Assert.Equal("Slot 1", licenses[0].Name);
    }

    [Fact]
    public void ImportSave_RefusesAFileThatIsNotASave()
    {
        var (service, fs, settings) = Create();
        settings.Get<MarioKartWiiEnums.Regions>(Arg.Any<Setting<MarioKartWiiEnums.Regions>>()).Returns(MarioKartWiiEnums.Regions.Europe);
        AddSave(fs, MarioKartWiiEnums.Regions.Europe, 0);
        service.LoadLicense();
        fs.Directory.CreateDirectory("/import");
        fs.File.WriteAllText("/import/rksys.dat", "not a save");

        Assert.True(service.ImportSave("/import/rksys.dat").IsFailure);
        Assert.False(fs.Directory.Exists("/saves/RMCP/backup"));
    }

    [Fact]
    public void ChangeProfileId_RefusesAProfileIdAnotherLicenseUses()
    {
        var (service, fs, settings) = Create();
        settings.Get<MarioKartWiiEnums.Regions>(Arg.Any<Setting<MarioKartWiiEnums.Regions>>()).Returns(MarioKartWiiEnums.Regions.Europe);
        settings.PathsSetupCorrectly().Returns(true);
        AddSave(fs, MarioKartWiiEnums.Regions.Europe, 0, 1);
        service.LoadLicense();

        var result = service.ChangeProfileId(0, 2);

        Assert.True(result.IsFailure);
        Assert.False(fs.Directory.Exists("/saves/RMCP/backup"));
    }

    [Fact]
    public void ALicense_ExportsAsRkp_AndImportsIntoAnotherSlot_AfterABackup()
    {
        var (service, fs, settings) = Create();
        settings.Get<MarioKartWiiEnums.Regions>(Arg.Any<Setting<MarioKartWiiEnums.Regions>>()).Returns(MarioKartWiiEnums.Regions.Europe);
        settings.PathsSetupCorrectly().Returns(true);
        AddSave(fs, MarioKartWiiEnums.Regions.Europe, 0, 1);
        Assert.True(service.LoadLicense().IsSuccess);
        var save = fs.File.ReadAllBytes("/saves/RMCP/rksys.dat");
        fs.Directory.CreateDirectory("/out");

        Assert.True(service.ExportLicense(1, "/out/slot2.rkp").IsSuccess);

        // The file is the license's RKPD block, byte for byte.
        var rkp = fs.File.ReadAllBytes("/out/slot2.rkp");
        Assert.Equal(save.AsSpan(8 + 0x8CC0, 0x8CC0).ToArray(), rkp);
        var file = service.ReadLicenseFile("/out/slot2.rkp");
        Assert.True(file.IsSuccess);
        Assert.Equal(("Slot 2", 2u), (file.Value.Name, file.Value.ProfileId));

        var imported = service.ImportLicense(3, "/out/slot2.rkp");

        Assert.True(imported.IsSuccess, imported.IsFailure ? imported.Error.Message : null);
        var after = fs.File.ReadAllBytes("/saves/RMCP/rksys.dat");
        Assert.Equal(rkp, after.AsSpan(8 + 3 * 0x8CC0, 0x8CC0).ToArray());
        // The other licenses are untouched and the save still passes the game's checksum.
        Assert.Equal(save.AsSpan(8, 2 * 0x8CC0).ToArray(), after.AsSpan(8, 2 * 0x8CC0).ToArray());
        Assert.Equal(Crc.ComputeCrc32(after, 0, 0x27FFC), BigEndianBinary.BufferToUint32(after, 0x27FFC));
        Assert.Single(fs.Directory.GetDirectories("/saves/RMCP/backup", "*-before-license-import"));
        Assert.Contains(service.GetLicenseIdentities(), license => license.Slot == 3 && license.ProfileId == 2);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(0x8CC0 + 1)]
    public void ImportLicense_RefusesFilesThatAreNotOneLicense(int length)
    {
        var (service, fs, settings) = Create();
        settings.Get<MarioKartWiiEnums.Regions>(Arg.Any<Setting<MarioKartWiiEnums.Regions>>()).Returns(MarioKartWiiEnums.Regions.Europe);
        settings.PathsSetupCorrectly().Returns(true);
        AddSave(fs, MarioKartWiiEnums.Regions.Europe, 0);
        Assert.True(service.LoadLicense().IsSuccess);
        var save = fs.File.ReadAllBytes("/saves/RMCP/rksys.dat");
        var bad = new byte[length];
        Encoding.ASCII.GetBytes("RKPD").CopyTo(bad, 0);
        BigEndianBinary.WriteUInt32BigEndian(bad, 0x5C, 5);
        fs.File.WriteAllBytes("/bad.rkp", bad);

        Assert.True(service.ImportLicense(0, "/bad.rkp").IsFailure);
        Assert.True(service.ReadLicenseFile("/bad.rkp").IsFailure);
        Assert.Equal(save, fs.File.ReadAllBytes("/saves/RMCP/rksys.dat"));
        Assert.False(fs.Directory.Exists("/saves/RMCP/backup"));
    }

    // Sets the ids of a license in a save file directly, to build the situations a real save can be in.
    private static void SetIds(MockFileSystem fs, string path, int slot, uint? profile = null, uint? pseudo = null, uint? authentic = null)
    {
        var data = fs.File.ReadAllBytes(path);
        var offset = 8 + slot * 0x8CC0;
        if (profile is { } p)
            BigEndianBinary.WriteUInt32BigEndian(data, offset + 0x5C, p);
        if (pseudo is { } a)
            BigEndianBinary.WriteUInt32BigEndian(data, offset + 0x4C, a);
        if (authentic is { } b)
            BigEndianBinary.WriteUInt32BigEndian(data, offset + 0x58, b);
        fs.File.WriteAllBytes(path, data);
    }

    [Fact]
    public void TheIdCheck_FindsSharedIds_AndIdsThatDiffer()
    {
        LicenseIdentity[] all =
        [
            new(0, "a", 10, "fc-a", 100, 100),
            new(1, "b", 11, "fc-b", 100, 100), // shares pseudo and authentic with a
            new(2, "c", 12, "fc-c", 300, 301), // its two ids differ
            new(3, "d", 10, "fc-d", 400, 400), // the same friend code as a
        ];

        Assert.False(LicenseIds.Check(new(9, "z", 99, "fc-z", 500, 500), all).Any);
        Assert.Equal([1], LicenseIds.Check(all[0], all).SharesWith.Where(o => o.ProfileId != 10).Select(o => o.Slot));
        Assert.True(LicenseIds.Check(all[2], all).IdsDiffer);
        Assert.True(LicenseIds.Check(all[0], all).SameFriendCode(all[0]));
        Assert.False(LicenseIds.Check(all[1], all).SameFriendCode(all[1]));
        // The next free value skips both ids and every id the others use.
        Assert.Equal(302u, LicenseIds.NextFree(300, 301, all.Where(o => o.Slot != 2)));
        Assert.Equal(304u, LicenseIds.NextFree(300, 301, [new(5, "e", 50, "fc-e", 302, 303)]));
    }

    [Fact]
    public void MakeIdsUnique_GivesNewSharedIds_KeepsTheFriendCode_AndFixesTheSameLicenseOnOtherRegions()
    {
        var (service, fs, settings) = Create();
        settings.Get<MarioKartWiiEnums.Regions>(Arg.Any<Setting<MarioKartWiiEnums.Regions>>()).Returns(MarioKartWiiEnums.Regions.Europe);
        settings.PathsSetupCorrectly().Returns(true);
        AddSave(fs, MarioKartWiiEnums.Regions.Europe, 0, 1);
        AddSave(fs, MarioKartWiiEnums.Regions.America, 0, 1);
        // License 2 got the ids of license 1 (a copy that kept them), on both regions' saves.
        SetIds(fs, "/saves/RMCP/rksys.dat", 1, pseudo: 100, authentic: 100);
        SetIds(fs, "/saves/RMCE/rksys.dat", 1, pseudo: 100, authentic: 100);
        Assert.True(service.LoadLicense().IsSuccess);
        var before = service.GetLicenseIdentities().Single(l => l.Slot == 1);
        Assert.True(LicenseIds.Check(before, service.GetLicenseIdentities()).Any);

        var result = service.MakeIdsUnique(1);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        var identities = service.GetLicenseIdentities();
        var fixedLicense = identities.Single(l => l.Slot == 1);
        Assert.Equal(before.FriendCode, fixedLicense.FriendCode);
        Assert.Equal(fixedLicense.PseudoId, fixedLicense.AuthenticId);
        Assert.False(LicenseIds.Check(fixedLicense, identities).Any);
        Assert.Equal(100u, identities.Single(l => l.Slot == 0).PseudoId);
        Assert.Single(fs.Directory.GetDirectories("/saves/RMCP/backup", "*-change"));
        // The same license on the other region got the same new ids; its other license was not touched.
        var america = fs.File.ReadAllBytes("/saves/RMCE/rksys.dat");
        Assert.Equal(fixedLicense.PseudoId, BigEndianBinary.BufferToUint32(america, 8 + 0x8CC0 + 0x4C));
        Assert.Equal(100u, BigEndianBinary.BufferToUint32(america, 8 + 0x4C));
        // Fixed already: nothing more to do.
        Assert.True(service.MakeIdsUnique(1).IsFailure);
    }

    [Fact]
    public void MakeIdsUnique_RefusesWhenTheFriendCodeItselfIsShared_OrTheSlotIsEmpty()
    {
        var (service, fs, settings) = Create();
        settings.Get<MarioKartWiiEnums.Regions>(Arg.Any<Setting<MarioKartWiiEnums.Regions>>()).Returns(MarioKartWiiEnums.Regions.Europe);
        settings.PathsSetupCorrectly().Returns(true);
        AddSave(fs, MarioKartWiiEnums.Regions.Europe, 0, 1);
        SetIds(fs, "/saves/RMCP/rksys.dat", 1, profile: 1);
        Assert.True(service.LoadLicense().IsSuccess);
        var save = fs.File.ReadAllBytes("/saves/RMCP/rksys.dat");

        Assert.True(service.MakeIdsUnique(1).IsFailure);
        Assert.True(service.MakeIdsUnique(3).IsFailure);
        Assert.True(service.MakeIdsUnique(9).IsFailure);
        Assert.Equal(save, fs.File.ReadAllBytes("/saves/RMCP/rksys.dat"));
        Assert.False(fs.Directory.Exists("/saves/RMCP/backup"));
    }

    [Fact]
    public void ChangingACode_LeavesADifferentLicenseOnAnotherRegionAlone_AndFindsTheSameOneInAnySlot()
    {
        var (service, fs, settings) = Create();
        settings.Get<MarioKartWiiEnums.Regions>(Arg.Any<Setting<MarioKartWiiEnums.Regions>>()).Returns(MarioKartWiiEnums.Regions.Europe);
        settings.PathsSetupCorrectly().Returns(true);
        AddSave(fs, MarioKartWiiEnums.Regions.Europe, 0, 1);
        // America: slot 1 is another license altogether (its own code), and the same one as Europe's slot 1 is in slot 3.
        AddSave(fs, MarioKartWiiEnums.Regions.America, 0, 2);
        SetIds(fs, "/saves/RMCE/rksys.dat", 0, profile: 999);
        SetIds(fs, "/saves/RMCE/rksys.dat", 2, profile: 2);
        Assert.True(service.LoadLicense().IsSuccess);

        // Europe's license 1 (code 1) is not the one in America's slot 1 (code 999), so that stays as it is.
        Assert.True(service.ChangeProfileId(0, 200_000).IsSuccess);
        var america = fs.File.ReadAllBytes("/saves/RMCE/rksys.dat");
        Assert.Equal(999u, BigEndianBinary.BufferToUint32(america, 8 + 0x5C));
        Assert.False(fs.Directory.Exists("/saves/RMCE/backup"));

        // Europe's license 2 (code 2) is America's slot 3: found there, whatever its slot.
        Assert.True(service.ChangeProfileId(1, 300_000).IsSuccess);
        america = fs.File.ReadAllBytes("/saves/RMCE/rksys.dat");
        Assert.Equal(300_000u, BigEndianBinary.BufferToUint32(america, 8 + 2 * 0x8CC0 + 0x5C));
        Assert.Equal(999u, BigEndianBinary.BufferToUint32(america, 8 + 0x5C));
    }

    [Fact]
    public void ALicenseFromAFileOrSave_CarriesItsIds_SoClashesCanBeSeenBeforeImporting()
    {
        var (service, fs, settings) = Create();
        settings.Get<MarioKartWiiEnums.Regions>(Arg.Any<Setting<MarioKartWiiEnums.Regions>>()).Returns(MarioKartWiiEnums.Regions.Europe);
        settings.PathsSetupCorrectly().Returns(true);
        AddSave(fs, MarioKartWiiEnums.Regions.Europe, 0);
        Assert.True(service.LoadLicense().IsSuccess);
        fs.Directory.CreateDirectory("/out");
        Assert.True(service.ExportLicense(0, "/out/a.rkp").IsSuccess);

        var file = service.ReadLicenseFile("/out/a.rkp").Value;
        var inSave = service.ReadSaveLicenses("/saves/RMCP/rksys.dat").Single();

        Assert.Equal((100u, 100u), (file.PseudoId, file.AuthenticId));
        Assert.Equal((100u, 100u), (inSave.PseudoId, inSave.AuthenticId));
    }

    [Fact]
    public void ALicense_CanBeTakenFromAnotherSave_IntoAnySlot_WithoutTouchingTheRest()
    {
        var (service, fs, settings) = Create();
        settings.Get<MarioKartWiiEnums.Regions>(Arg.Any<Setting<MarioKartWiiEnums.Regions>>()).Returns(MarioKartWiiEnums.Regions.Europe);
        settings.PathsSetupCorrectly().Returns(true);
        AddSave(fs, MarioKartWiiEnums.Regions.Europe, 0, 1);
        // Another save, as a backup or a file from elsewhere would be: licenses in slots 2 and 3 (profile ids 2 and 3).
        AddSave(fs, MarioKartWiiEnums.Regions.Japan, 1, 2);
        const string other = "/saves/RMCJ/rksys.dat";
        Assert.True(service.LoadLicense().IsSuccess);
        var before = fs.File.ReadAllBytes("/saves/RMCP/rksys.dat");

        var found = service.ReadSaveLicenses(other);
        Assert.Equal([1, 2], found.Select(license => license.Slot));
        Assert.Equal(["Slot 2", "Slot 3"], found.Select(license => license.Name));

        var result = service.ImportLicenseFromSave(3, other, 2);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        var after = fs.File.ReadAllBytes("/saves/RMCP/rksys.dat");
        var source = fs.File.ReadAllBytes(other);
        Assert.Equal(source.AsSpan(8 + 2 * 0x8CC0, 0x8CC0).ToArray(), after.AsSpan(8 + 3 * 0x8CC0, 0x8CC0).ToArray());
        Assert.Equal(before.AsSpan(8, 2 * 0x8CC0).ToArray(), after.AsSpan(8, 2 * 0x8CC0).ToArray());
        Assert.Equal(Crc.ComputeCrc32(after, 0, 0x27FFC), BigEndianBinary.BufferToUint32(after, 0x27FFC));
        Assert.Single(fs.Directory.GetDirectories("/saves/RMCP/backup", "*-before-license-import"));
        Assert.Contains(service.GetLicenseIdentities(), license => license is { Slot: 3, ProfileId: 3 });
        // The other save itself is never changed.
        Assert.Equal(source, fs.File.ReadAllBytes(other));
    }

    [Fact]
    public void SeveralRkpFiles_GoIntoTheGivenSlots_AfterOneBackup_AndABadFileChangesNothing()
    {
        var (service, fs, settings) = Create();
        settings.Get<MarioKartWiiEnums.Regions>(Arg.Any<Setting<MarioKartWiiEnums.Regions>>()).Returns(MarioKartWiiEnums.Regions.Europe);
        settings.PathsSetupCorrectly().Returns(true);
        AddSave(fs, MarioKartWiiEnums.Regions.Europe, 0);
        AddSave(fs, MarioKartWiiEnums.Regions.Japan, 1, 2);
        Assert.True(service.LoadLicense().IsSuccess);
        var other = fs.File.ReadAllBytes("/saves/RMCJ/rksys.dat");
        fs.Directory.CreateDirectory("/in");
        fs.File.WriteAllBytes("/in/a.rkp", other.AsSpan(8 + 0x8CC0, 0x8CC0).ToArray());
        fs.File.WriteAllBytes("/in/b.rkp", other.AsSpan(8 + 2 * 0x8CC0, 0x8CC0).ToArray());
        fs.File.WriteAllText("/in/notes.rkp", "not a license");
        var before = fs.File.ReadAllBytes("/saves/RMCP/rksys.dat");

        Assert.True(service.ImportLicenseFiles([(1, "/in/a.rkp"), (2, "/in/notes.rkp")]).IsFailure);
        Assert.True(service.ImportLicenseFiles([(1, "/in/a.rkp"), (1, "/in/b.rkp")]).IsFailure);
        Assert.True(service.ImportLicenseFiles([]).IsFailure);
        Assert.Equal(before, fs.File.ReadAllBytes("/saves/RMCP/rksys.dat"));
        Assert.False(fs.Directory.Exists("/saves/RMCP/backup"));

        var result = service.ImportLicenseFiles([(1, "/in/a.rkp"), (3, "/in/b.rkp")]);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        var after = fs.File.ReadAllBytes("/saves/RMCP/rksys.dat");
        Assert.Equal(other.AsSpan(8 + 0x8CC0, 0x8CC0).ToArray(), after.AsSpan(8 + 0x8CC0, 0x8CC0).ToArray());
        Assert.Equal(other.AsSpan(8 + 2 * 0x8CC0, 0x8CC0).ToArray(), after.AsSpan(8 + 3 * 0x8CC0, 0x8CC0).ToArray());
        Assert.Equal(before.AsSpan(8, 0x8CC0).ToArray(), after.AsSpan(8, 0x8CC0).ToArray());
        Assert.Single(fs.Directory.GetDirectories("/saves/RMCP/backup", "*-before-license-import"));
    }

    [Fact]
    public void Backups_CanBeNamedAndDeleted_AndOldOnesAreRemovedAutomatically_ExceptNamedAndHistory()
    {
        var (service, fs, settings) = Create();
        settings.Get<MarioKartWiiEnums.Regions>(Arg.Any<Setting<MarioKartWiiEnums.Regions>>()).Returns(MarioKartWiiEnums.Regions.Europe);
        settings.PathsSetupCorrectly().Returns(true);
        AddSave(fs, MarioKartWiiEnums.Regions.Europe, 0, 1);
        Assert.True(service.LoadLicense().IsSuccess);

        // A friend code change is the history and a named backup is wanted: neither is ever cleared out.
        Assert.True(service.ChangeProfileId(0, 200_000).IsSuccess);
        var first = service.BackupSaveNow().Value;
        Assert.True(service.NameBackup(first, "  before the tournament ").IsSuccess);
        for (var i = 0; i < 40; i++)
            Assert.True(service.BackupSaveNow().IsSuccess);

        var backups = service.ListBackups();
        // 30 ordinary backups, the named one and the one made by the change.
        Assert.Equal(32, backups.Count);
        Assert.Equal("before the tournament", backups.Single(b => b.Name == first).Label);
        Assert.Single(backups, b => b.Kind == "change");

        Assert.Equal(25, service.PruneBackups(5));
        backups = service.ListBackups();
        Assert.Equal(7, backups.Count);
        Assert.Contains(backups, b => b.Name == first);

        Assert.True(service.NameBackup(first, "").IsSuccess);
        Assert.Null(service.ListBackups().Single(b => b.Name == first).Label);
        Assert.True(service.DeleteBackup(first).IsSuccess);
        Assert.DoesNotContain(service.ListBackups(), b => b.Name == first);
        Assert.True(service.DeleteBackup(first).IsFailure);
        Assert.True(service.NameBackup("missing", "x").IsFailure);
    }

    [Fact]
    public void SeveralLicenses_CanBeTakenFromAnotherSave_Together_EachIntoItsOwnSlot_AfterOneBackup()
    {
        var (service, fs, settings) = Create();
        settings.Get<MarioKartWiiEnums.Regions>(Arg.Any<Setting<MarioKartWiiEnums.Regions>>()).Returns(MarioKartWiiEnums.Regions.Europe);
        settings.PathsSetupCorrectly().Returns(true);
        AddSave(fs, MarioKartWiiEnums.Regions.Europe, 0, 1);
        AddSave(fs, MarioKartWiiEnums.Regions.Japan, 1, 2);
        const string other = "/saves/RMCJ/rksys.dat";
        Assert.True(service.LoadLicense().IsSuccess);
        var before = fs.File.ReadAllBytes("/saves/RMCP/rksys.dat");

        // Slot 0 is not in the other save, so nothing is taken, backed up or written.
        Assert.True(service.ImportLicensesFromSave(other, [1, 0]).IsFailure);
        Assert.Equal(before, fs.File.ReadAllBytes("/saves/RMCP/rksys.dat"));
        Assert.False(fs.Directory.Exists("/saves/RMCP/backup"));

        var result = service.ImportLicensesFromSave(other, [1, 2]);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        var after = fs.File.ReadAllBytes("/saves/RMCP/rksys.dat");
        var source = fs.File.ReadAllBytes(other);
        Assert.Equal(source.AsSpan(8 + 0x8CC0, 2 * 0x8CC0).ToArray(), after.AsSpan(8 + 0x8CC0, 2 * 0x8CC0).ToArray());
        Assert.Equal(before.AsSpan(8, 0x8CC0).ToArray(), after.AsSpan(8, 0x8CC0).ToArray());
        Assert.Equal(Crc.ComputeCrc32(after, 0, 0x27FFC), BigEndianBinary.BufferToUint32(after, 0x27FFC));
        Assert.Single(fs.Directory.GetDirectories("/saves/RMCP/backup", "*-before-license-import"));
        Assert.True(service.ImportLicensesFromSave(other, []).IsFailure);
        Assert.True(service.ImportLicensesFromSave(other, [4]).IsFailure);
    }

    [Fact]
    public void ImportLicenseFromSave_RefusesAnEmptySlot_ANonSave_AndBadSlots_WithoutChangingAnything()
    {
        var (service, fs, settings) = Create();
        settings.Get<MarioKartWiiEnums.Regions>(Arg.Any<Setting<MarioKartWiiEnums.Regions>>()).Returns(MarioKartWiiEnums.Regions.Europe);
        settings.PathsSetupCorrectly().Returns(true);
        AddSave(fs, MarioKartWiiEnums.Regions.Europe, 0);
        AddSave(fs, MarioKartWiiEnums.Regions.Japan, 1);
        fs.File.WriteAllText("/notes.txt", "not a save");
        Assert.True(service.LoadLicense().IsSuccess);
        var save = fs.File.ReadAllBytes("/saves/RMCP/rksys.dat");

        Assert.True(service.ImportLicenseFromSave(2, "/saves/RMCJ/rksys.dat", 0).IsFailure);
        Assert.True(service.ImportLicenseFromSave(2, "/notes.txt", 0).IsFailure);
        Assert.True(service.ImportLicenseFromSave(2, "/missing.dat", 0).IsFailure);
        Assert.True(service.ImportLicenseFromSave(4, "/saves/RMCJ/rksys.dat", 1).IsFailure);
        Assert.True(service.ImportLicenseFromSave(0, "/saves/RMCJ/rksys.dat", 7).IsFailure);
        Assert.Empty(service.ReadSaveLicenses("/notes.txt"));
        Assert.Empty(service.ReadSaveLicenses("/missing.dat"));
        Assert.Equal(save, fs.File.ReadAllBytes("/saves/RMCP/rksys.dat"));
        Assert.False(fs.Directory.Exists("/saves/RMCP/backup"));
    }

    [Fact]
    public void ABackupsLicense_CanBeTakenBack_FromTheBackupList()
    {
        var (service, fs, settings) = Create();
        settings.Get<MarioKartWiiEnums.Regions>(Arg.Any<Setting<MarioKartWiiEnums.Regions>>()).Returns(MarioKartWiiEnums.Regions.Europe);
        settings.PathsSetupCorrectly().Returns(true);
        AddSave(fs, MarioKartWiiEnums.Regions.Europe, 0, 1);
        Assert.True(service.LoadLicense().IsSuccess);
        var original = service.GetLicenseIdentities().Single(license => license.Slot == 0);
        Assert.True(service.ChangeProfileId(0, 200_000).IsSuccess);

        // The old code lives on in the backup made before the change: bring it into the empty slot 3 as well.
        var backup = service.ListBackups().Single(item => item.Kind == "change");
        var old = service.GetBackupLicenses(backup.Name).Single(license => license.Slot == 0);
        Assert.Equal(original.FriendCode, old.FriendCode);
        Assert.True(service.ImportLicenseFromSave(3, backup.FilePath, old.Slot).IsSuccess);

        var copy = service.GetLicenseIdentities().Single(license => license.Slot == 3);
        Assert.Equal(original.FriendCode, copy.FriendCode);
        Assert.Equal(200_000u, service.GetLicenseIdentities().Single(license => license.Slot == 0).ProfileId);
    }

    [Fact]
    public void History_ListsEveryFriendCodeChange_NewestFirst_EvenWithinOneSecond()
    {
        var (service, fs, settings) = Create();
        settings.Get<MarioKartWiiEnums.Regions>(Arg.Any<Setting<MarioKartWiiEnums.Regions>>()).Returns(MarioKartWiiEnums.Regions.Europe);
        settings.PathsSetupCorrectly().Returns(true);
        AddSave(fs, MarioKartWiiEnums.Regions.Europe, 0, 1);
        Assert.True(service.LoadLicense().IsSuccess);
        var first = service.GetLicenseIdentities().Single(i => i.Slot == 0).FriendCode;
        var second = service.GetLicenseIdentities().Single(i => i.Slot == 1).FriendCode;
        Assert.Empty(service.GetFriendCodeHistory());

        // A manual backup changes nothing, so it adds nothing to the history.
        Assert.True(service.BackupSaveNow().IsSuccess);
        Assert.True(service.ChangeProfileId(0, 200_000).IsSuccess);
        var changed = service.GetLicenseIdentities().Single(i => i.Slot == 0).FriendCode;
        fs.Directory.CreateDirectory("/out");
        Assert.True(service.ExportLicense(1, "/out/two.rkp").IsSuccess);
        Assert.True(service.ImportLicense(3, "/out/two.rkp").IsSuccess);
        Assert.True(service.UndoLastProfileIdChange().IsSuccess);

        var history = service.GetFriendCodeHistory();

        Assert.Equal(
            [("undo", 0, changed, first), ("before-license-import", 3, "", second), ("change", 0, first, changed)],
            history.Select(h => (h.Kind, h.Slot, h.OldCode, h.NewCode))
        );
        Assert.Equal("Slot 2", history[1].Name);
        Assert.True(history[0].Time > history[1].Time && history[1].Time > history[2].Time);
    }

    [Fact]
    public void RestoreFriendCode_PutsAnOldCodeBack_OnEveryRegion_AndUndoTakesItBack()
    {
        var (service, fs, settings) = Create();
        settings.Get<MarioKartWiiEnums.Regions>(Arg.Any<Setting<MarioKartWiiEnums.Regions>>()).Returns(MarioKartWiiEnums.Regions.Europe);
        settings.PathsSetupCorrectly().Returns(true);
        AddSave(fs, MarioKartWiiEnums.Regions.Europe, 0, 1);
        AddSave(fs, MarioKartWiiEnums.Regions.America, 0);
        Assert.True(service.LoadLicense().IsSuccess);
        var original = service.GetLicenseIdentities().Single(i => i.Slot == 0);
        Assert.True(service.ChangeProfileId(0, 200_000).IsSuccess);
        Assert.True(service.ChangeProfileId(0, 300_000).IsSuccess);

        // Two changes later, put back the code from before the first one.
        var oldest = service.GetFriendCodeHistory().Last();
        Assert.Equal(original.FriendCode, oldest.OldCode);
        var restored = service.RestoreFriendCode(oldest.BackupName, oldest.Slot);

        Assert.True(restored.IsSuccess, restored.IsFailure ? restored.Error.Message : null);
        Assert.Equal(original, service.GetLicenseIdentities().Single(i => i.Slot == 0));
        Assert.Equal(original.ProfileId, BigEndianBinary.BufferToUint32(fs.File.ReadAllBytes("/saves/RMCE/rksys.dat"), 8 + 0x5C));
        Assert.Equal("restore-change", service.GetFriendCodeHistory()[0].Kind);
        // Restoring it again changes nothing.
        Assert.True(service.RestoreFriendCode(oldest.BackupName, oldest.Slot).IsFailure);

        Assert.True(service.UndoLastProfileIdChange().IsSuccess);
        Assert.Equal(300_000u, service.GetLicenseIdentities().Single(i => i.Slot == 0).ProfileId);
        Assert.Equal(300_000u, BigEndianBinary.BufferToUint32(fs.File.ReadAllBytes("/saves/RMCE/rksys.dat"), 8 + 0x5C));
    }

    [Fact]
    public void RestoreFriendCode_RefusesACodeAnotherLicenseHasNow()
    {
        var (service, fs, settings) = Create();
        settings.Get<MarioKartWiiEnums.Regions>(Arg.Any<Setting<MarioKartWiiEnums.Regions>>()).Returns(MarioKartWiiEnums.Regions.Europe);
        settings.PathsSetupCorrectly().Returns(true);
        AddSave(fs, MarioKartWiiEnums.Regions.Europe, 0, 1);
        Assert.True(service.LoadLicense().IsSuccess);
        Assert.True(service.ChangeProfileId(0, 200_000).IsSuccess);
        var change = service.GetFriendCodeHistory().Single();
        // License 2 has since taken the code license 1 used to have.
        Assert.True(service.ChangeProfileId(1, 1).IsSuccess);
        var save = fs.File.ReadAllBytes("/saves/RMCP/rksys.dat");

        Assert.True(service.RestoreFriendCode(change.BackupName, change.Slot).IsFailure);
        Assert.Equal(save, fs.File.ReadAllBytes("/saves/RMCP/rksys.dat"));
    }

    [Fact]
    public void ExportLicense_RefusesAnEmptySlot()
    {
        var (service, fs, settings) = Create();
        settings.Get<MarioKartWiiEnums.Regions>(Arg.Any<Setting<MarioKartWiiEnums.Regions>>()).Returns(MarioKartWiiEnums.Regions.Europe);
        AddSave(fs, MarioKartWiiEnums.Regions.Europe, 0);
        Assert.True(service.LoadLicense().IsSuccess);

        Assert.True(service.ExportLicense(2, "/out.rkp").IsFailure);
        Assert.False(fs.File.Exists("/out.rkp"));
    }

    private static void AddSave(MockFileSystem fs, MarioKartWiiEnums.Regions region, params int[] slots)
    {
        var data = new byte[0x2BC000];
        Encoding.ASCII.GetBytes("RKSD0006").CopyTo(data, 0);
        foreach (var slot in slots)
        {
            var offset = 8 + slot * 0x8CC0;
            Encoding.ASCII.GetBytes("RKPD").CopyTo(data, offset);
            Encoding.BigEndianUnicode.GetBytes($"Slot {slot + 1}").CopyTo(data, offset + 0x14);
            BigEndianBinary.WriteUInt32BigEndian(data, offset + 0x5C, (uint)(slot + 1));
            BigEndianBinary.WriteUInt32BigEndian(data, offset + 0x4C, (uint)(100 + slot));
            BigEndianBinary.WriteUInt32BigEndian(data, offset + 0x58, (uint)(100 + slot));
        }
        var path = $"/saves/{GameRegion.GetGameId(region)}/rksys.dat";
        fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(path)!);
        fs.File.WriteAllBytes(path, data);
    }

    private static (GameLicenseSingletonService, MockFileSystem, ISettingsManager) Create()
    {
        var fs = new MockFileSystem();
        var paths = Substitute.For<ICustomDistributionPaths>();
        paths.SaveFolderPath.Returns("/saves");
        var settings = Substitute.For<ISettingsManager>();
        var miis = Substitute.For<IMiiDbService>();
        miis.GetByAvatarId(Arg.Any<uint>()).Returns(Fail("Missing Mii"));
        var service = new GameLicenseSingletonService(
            miis,
            fs,
            Substitute.For<IWhWzDataSingletonService>(),
            Substitute.For<IRrRatingReader>(),
            settings,
            Substitute.For<ISaveRegionService>(),
            Substitute.For<IDolphinPaths>(),
            paths,
            Substitute.For<IRoomPresence>(),
            Substitute.For<IPollingScheduler>(),
            System.TimeProvider.System,
            NullLogger<GameLicenseSingletonService>.Instance
        );
        return (service, fs, settings);
    }
}
