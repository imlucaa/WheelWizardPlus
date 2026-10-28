namespace WheelWizard.DiscImages;

/// <summary>The colour of the line under a player's name online. The game picks it from the region ID.</summary>
public enum LineColor
{
    Red = 0,
    Blue = 1,
    Green = 2,
    Yellow = 3,
    White = 4,
    Purple = 5,
}

/// <summary>
/// Changes the region ID that a Mario Kart Wii <c>main.dol</c> loads, which is what decides the line colour online. The game
/// sets it with one "load immediate" instruction near the start of the file, so the byte to change is the value of that
/// instruction.
/// </summary>
public static class RegionLine
{
    // Japan and PAL have two: one for the country codes that get their own region ID (Taiwan, Australia and New Zealand), and
    // one for all the others. Both are set, so the colour does not depend on the country code.
    private static readonly Dictionary<string, int[]> Offsets = new(StringComparer.Ordinal)
    {
        ["RMCE"] = [0x6113],
        ["RMCP"] = [0x614F, 0x61AF],
        ["RMCJ"] = [0x60A3, 0x60D3],
        ["RMCK"] = [0x625F],
    };

    public static bool Supports(string gameId) => TryOffsets(gameId, out _);

    /// <summary>The colour a <c>main.dol</c> gives now, or null when it is not one of the six or the file does not fit the game.</summary>
    public static LineColor? Read(byte[] dol, string gameId)
    {
        if (!TryOffsets(gameId, out var offsets) || !offsets.All(offset => IsLoadImmediate(dol, offset)))
            return null;

        var value = dol[offsets[0]];
        return Enum.IsDefined(typeof(LineColor), (int)value) ? (LineColor)value : null;
    }

    /// <summary>Writes the colour into <paramref name="dol"/>. Nothing is changed when the file is not the game's own.</summary>
    public static OperationResult Apply(byte[] dol, string gameId, LineColor color)
    {
        if (!TryOffsets(gameId, out var offsets))
            return Fail("That is not a Mario Kart Wii disc.");
        if (!offsets.All(offset => IsLoadImmediate(dol, offset)))
            return Fail("This disc's main.dol is not the one Mario Kart Wii comes with, so nothing was changed.");

        foreach (var offset in offsets)
            dol[offset] = (byte)color;
        return Ok();
    }

    private static bool TryOffsets(string gameId, out int[] offsets)
    {
        offsets = [];
        return gameId is { Length: >= 4 } && Offsets.TryGetValue(gameId[..4], out offsets!);
    }

    // PowerPC "li rD,N" is "addi rD,0,N": opcode 14 in the first six bits, a zero source register, and the value in the
    // last two bytes (here always under 256).
    private static bool IsLoadImmediate(byte[] dol, int valueOffset) =>
        valueOffset >= 3
        && valueOffset < dol.Length
        && (dol[valueOffset - 3] & 0xFC) == 0x38
        && (dol[valueOffset - 2] & 0x1F) == 0
        && dol[valueOffset - 1] == 0;
}
