using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace WheelWizard.DiscordPresence;

/// <summary>
/// Reads which screen Mario Kart Wii is on out of Dolphin's emulated memory. It only ever reads, so the game and
/// the online server see nothing different. Windows only.
/// </summary>
public sealed class DolphinMemoryReader : IDisposable
{
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint MemCommit = 0x1000;
    private const uint MemMapped = 0x40000;
    private const long Mem1Size = 0x2000000;
    private const long Mem2Size = 0x4000000;

    // SectionMgr::sInstance per game region, from rr-pulsar's symbols.txt and versions.txt. Korea comes from the version
    // table of the Pulsar Pack Creator, which gives one offset for this whole part of memory (P minus 0x119C0) rather than
    // one per address like Europe and Japan, so it is checked against the game when read (see ReadSectionIdUnlocked).
    private static readonly Dictionary<char, uint> SectionMgrAddress = new()
    {
        ['P'] = 0x809C1E38,
        ['E'] = 0x809BD508,
        ['J'] = 0x809C0E98,
        ['K'] = 0x809B0478,
    };

    // RKSYS::Mgr::sInstance per region, translated with the same version table.
    private static readonly Dictionary<char, uint> RksysManagerAddress = new()
    {
        ['P'] = 0x809BD748,
        ['E'] = 0x809B8F88,
        ['J'] = 0x809BC7A8,
        ['K'] = 0x809ABD88,
    };

    private int _processId;
    private nint _handle;
    private long _mem1;
    private long _mem2;
    private char _region;
    private string _attachError = "";
    private readonly object _gate = new();

    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>Which of the four save licenses is selected in the game (0 to 3), or null when it cannot be read.</summary>
    public int? ReadLicenseIndex(int processId, bool recomp = false)
    {
        lock (_gate)
        {
            if (!IsSupported || !Attach(processId, recomp))
                return null;

            try
            {
                var save = ReadUInt32(RksysManagerAddress[_region]);
                if (save is null || !IsMainMemory(save.Value))
                    return null;

                // curLicenseId is the u16 at offset 0x36 of the save manager.
                var word = ReadUInt32(save.Value + 0x34);
                return word is { } value && (value & 0xFFFF) <= 3 ? (int)(value & 0xFFFF) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    // Retro Rewind keeps the name of the track in a small block of its own: the letters "PULL", a version, a track id
    // and then the name as plain text. Found by scanning Dolphin's memory; it stays at the same address between tracks.
    // The block is at a different address in each game region (found by scanning Dolphin's memory on a PAL game); a region
    // without a known address, or a pack version that moved it, is found by looking for the block itself.
    private static readonly Dictionary<char, uint> TrackBlockAddresses = new() { ['E'] = 0x804F22E0, ['P'] = 0x804F6900 };
    private const int TrackBlockSize = 12 + 64;
    private static readonly TimeSpan RescanEvery = TimeSpan.FromSeconds(15);
    private uint? _trackBlock;
    private DateTime _lastTrackScan = DateTime.MinValue;

    /// <summary>The name of the track the pack has loaded (for example "3DS Toad Circuit"), or null when it is not there.</summary>
    public string? ReadTrackName(int processId, bool recomp = false)
    {
        lock (_gate)
        {
            if (!IsSupported || !Attach(processId, recomp))
                return null;

            try
            {
                var address = _trackBlock ?? (TrackBlockAddresses.TryGetValue(_region, out var known) ? known : 0);
                if (address != 0 && TrackNameAt(address) is { } name)
                {
                    _trackBlock = address;
                    return name;
                }

                // Not where it should be: look for it, but not on every refresh (the scan reads the whole memory).
                if (DateTime.UtcNow - _lastTrackScan < RescanEvery)
                    return null;
                _lastTrackScan = DateTime.UtcNow;
                _trackBlock = FindTrackBlocks(1).FirstOrDefault().Address is var found and not 0 ? found : null;
                return _trackBlock is { } scanned ? TrackNameAt(scanned) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    private string? TrackNameAt(uint address)
    {
        var block = new byte[TrackBlockSize];
        for (var offset = 0; offset < TrackBlockSize; offset += 4)
        {
            if (ReadUInt32(address + (uint)offset) is not { } word)
                return null;
            BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(offset, 4), word);
        }

        return TrackNameFromBlock(block);
    }

    /// <summary>Reads the track name out of the pack's block, or null when the block is not the pack's or holds no name.</summary>
    public static string? TrackNameFromBlock(ReadOnlySpan<byte> block)
    {
        if (block.Length < 13 || !block[..4].SequenceEqual("PULL"u8))
            return null;

        var text = block[12..];
        var end = text.IndexOf((byte)0);
        if (end >= 0)
            text = text[..end];
        // A real name is short, printable text; anything else is leftover data, not a track.
        if (text.Length == 0 || text.Length >= 64 || text.ToArray().Any(value => value is < 32 or > 126))
            return null;

        var name = System.Text.Encoding.ASCII.GetString(text).Trim();
        return name.Length == 0 ? null : name;
    }

    /// <summary>True while Mario Kart Wii is found in Dolphin, even when the screen cannot be read for a moment.</summary>
    public bool GameFound
    {
        get
        {
            lock (_gate)
                return _handle != 0 && _mem1 != 0 && _mem2 != 0;
        }
    }

    /// <summary>The current section id, or null when it cannot be read (not Mario Kart Wii yet, or unsupported).</summary>
    public int? ReadSectionId(int processId, bool recomp = false)
    {
        lock (_gate)
            return ReadSectionIdUnlocked(processId, recomp);
    }

    private int? ReadSectionIdUnlocked(int processId, bool recomp)
    {
        if (!IsSupported)
            return null;

        try
        {
            if (!Attach(processId, recomp))
                return null;

            var manager = ReadUInt32(SectionMgrAddress[_region]);
            if (manager is null || !IsMainMemory(manager.Value))
                return null;

            var section = ReadUInt32(manager.Value);
            if (section is null || !IsMainMemory(section.Value))
                return null;

            // Real section ids are small. A larger value means the address is wrong for this game version, so it is
            // treated as unreadable rather than shown as a made-up screen.
            return ReadUInt32(section.Value + 0) is { } id and < 256 ? (int)id : null;
        }
        catch (Exception)
        {
            Detach();
            return null;
        }
    }

    // WiiCompiled keeps the whole 4 GiB guest address space at one fixed place in its own process (a guest address is
    // just this base plus the address), so MEM1 and MEM2 are found without searching.
    private const long RecompGuestBase = 0x1000_0000_0000;

    private bool _recomp;

    private bool Attach(int processId, bool recomp)
    {
        // A stopped or restarted game leaves the remembered locations pointing at nothing, so confirm the game is
        // still there before trusting them; otherwise look for it again.
        if (_handle != 0 && _processId == processId && _recomp == recomp && _mem1 != 0 && _mem2 != 0 && StillMarioKartWii())
            return true;

        Detach();
        _handle = OpenProcess(ProcessVmRead | ProcessQueryInformation, false, processId);
        if (_handle == 0)
        {
            _attachError = "Could not open the Dolphin process. If one of the two programs runs as administrator, run both the same way.";
            return false;
        }
        _processId = processId;
        _recomp = recomp;

        if (recomp)
        {
            if (!IsMarioKartWii(RecompGuestBase + 0x80000000))
            {
                Detach();
                _attachError = "WiiCompiled is open but its game memory could not be read yet.";
                return false;
            }

            _mem1 = RecompGuestBase + 0x80000000;
            _mem2 = RecompGuestBase + 0x90000000;
            return true;
        }

        // MEM1 is a 32 MB mapped region starting with the game id; MEM2 (64 MB) is the next such region after it.
        // The section manager lives in MEM2, so both are needed.
        long address = 0;
        while (VirtualQueryEx(_handle, (nint)address, out var info, (nuint)Marshal.SizeOf<MemoryBasicInformation>()) != 0)
        {
            var baseAddress = (long)info.BaseAddress;
            var size = (long)info.RegionSize;
            if (info.State == MemCommit && info.Type == MemMapped)
            {
                if (_mem1 == 0 && size >= Mem1Size && IsMarioKartWii(baseAddress))
                    _mem1 = baseAddress;
                else if (_mem1 != 0 && size == Mem2Size)
                {
                    _mem2 = baseAddress;
                    return true;
                }
            }

            var next = baseAddress + size;
            if (next <= address)
                break;
            address = next;
        }

        Detach();
        _attachError =
            _mem1 == 0
                ? "Dolphin is open but no Mario Kart Wii game memory was found. Is the game loaded, and is it a supported region (USA, Europe, Japan, Korea)?"
                : "Found the game but not its second memory block.";
        return false;
    }

    private bool StillMarioKartWii()
    {
        var id = new byte[4];
        return ReadProcessMemory(_handle, (nint)_mem1, id, 4, out var read)
            && read == 4
            && id[0] == 'R'
            && id[1] == 'M'
            && id[2] == 'C'
            && id[3] == _region;
    }

    /// <summary>A plain-language report of each step, for the Discord settings page.</summary>
    public string Diagnose(int processId, bool recomp = false)
    {
        lock (_gate)
            return DiagnoseUnlocked(processId, recomp);
    }

    private string DiagnoseUnlocked(int processId, bool recomp)
    {
        if (!IsSupported)
            return "Reading the game screen only works on Windows.";

        try
        {
            _attachError = "";
            if (!Attach(processId, recomp))
                return _attachError;

            var manager = ReadUInt32(SectionMgrAddress[_region]);
            if (manager is null || !IsMainMemory(manager.Value))
                return $"Game found (region {_region}) but the menu manager is not ready yet.";

            var section = ReadUInt32(manager.Value);
            if (section is null || !IsMainMemory(section.Value))
                return $"Game found (region {_region}) but no screen is loaded yet.";

            return $"Connected. Region {_region}, screen id {ReadUInt32(section.Value)}.{Environment.NewLine}{DescribeTrackBlock()}";
        }
        catch (Exception exception)
        {
            Detach();
            return $"Could not read the game: {exception.Message}";
        }
    }

    // For the Check button: what sits where the pack keeps the track name, and where else a block like it is.
    private string DescribeTrackBlock()
    {
        var address = _trackBlock ?? (TrackBlockAddresses.TryGetValue(_region, out var known) ? known : 0);
        if (address != 0 && TrackNameAt(address) is { } name)
            return $"Track name: \"{name}\" (at 0x{address:X8}).";

        var found = FindTrackBlocks(5);
        return (address == 0 ? "Track name: no known address for this region. " : $"Track name: nothing at 0x{address:X8}. ")
            + (
                found.Count == 0
                    ? "No pack block found in memory."
                    : $"Pack blocks found at {string.Join(", ", found.Select(block => $"0x{block.Address:X8} (\"{block.Name}\")"))}."
            );
    }

    // Looks through the game's main memory for the pack's block: "PULL", then a readable name 12 bytes on.
    private List<(uint Address, string Name)> FindTrackBlocks(int max)
    {
        const int chunk = 1 << 20;
        var results = new List<(uint, string)>();
        var buffer = new byte[chunk + TrackBlockSize];
        for (long start = 0; start < 0x01800000 && results.Count < max; start += chunk)
        {
            var size = (nuint)Math.Min(buffer.Length, 0x01800000 - start);
            if (!ReadProcessMemory(_handle, (nint)(_mem1 + start), buffer, size, out var read) || read < 16)
                continue;

            for (var i = 0; i + 16 <= (int)read && results.Count < max; i += 4)
            {
                if (buffer[i] != (byte)'P' || buffer[i + 1] != (byte)'U' || buffer[i + 2] != (byte)'L' || buffer[i + 3] != (byte)'L')
                    continue;
                var end = Math.Min((int)read, i + TrackBlockSize);
                if (TrackNameFromBlock(buffer.AsSpan(i, end - i)) is { } name)
                    results.Add(((uint)(0x80000000 + start + i), name));
            }
        }

        return results;
    }

    private bool IsMarioKartWii(long baseAddress)
    {
        var id = new byte[4];
        if (!ReadProcessMemory(_handle, (nint)baseAddress, id, 4, out _))
            return false;
        var text = Encoding.ASCII.GetString(id);
        if (text.Length != 4 || !text.StartsWith("RMC", StringComparison.Ordinal) || !SectionMgrAddress.ContainsKey(text[3]))
            return false;
        _region = text[3];
        return true;
    }

    private static bool IsMainMemory(uint address) => address is >= 0x80000000 and < 0x81800000 or >= 0x90000000 and < 0x94000000;

    private uint? ReadUInt32(uint address)
    {
        var host = address switch
        {
            >= 0x80000000 and < 0x81800000 => _mem1 + (address & 0x01FFFFFF),
            >= 0x90000000 and < 0x94000000 => _mem2 + (address & 0x03FFFFFF),
            _ => 0,
        };
        if (host == 0)
            return null;

        var buffer = new byte[4];
        if (!ReadProcessMemory(_handle, (nint)host, buffer, 4, out var read) || read != 4)
            return null;
        return BinaryPrimitives.ReadUInt32BigEndian(buffer);
    }

    private void Detach()
    {
        if (_handle != 0)
            CloseHandle(_handle);
        _handle = 0;
        _mem1 = 0;
        _mem2 = 0;
        _processId = 0;
        _trackBlock = null;
        _lastTrackScan = DateTime.MinValue;
    }

    public void Dispose() => Detach();

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQueryEx(nint process, nint address, out MemoryBasicInformation buffer, nuint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(nint process, nint address, byte[] buffer, nuint size, out nuint read);
}
