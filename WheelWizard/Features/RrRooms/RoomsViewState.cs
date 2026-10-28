using System.Globalization;

namespace WheelWizard.RrRooms;

/// <summary>What the Rooms page keeps between launches: the sort, the average VR range and the friends-only switch.</summary>
public sealed record RoomsViewState(int Sort = 0, int? MinimumVr = null, int? MaximumVr = null, bool FriendsOnly = false)
{
    private const int LastSort = 4;

    /// <summary>Reads a saved value; anything missing or damaged gives the default view.</summary>
    public static RoomsViewState Parse(string? text)
    {
        var parts = (text ?? string.Empty).Split('|');
        if (
            parts.Length is not (4 or 5)
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var sort)
            || sort > LastSort
        )
            return new RoomsViewState();

        // A fifth part, from a build that had an auto refresh switch, is ignored: the list always refreshes now.
        return new RoomsViewState(sort, Vr(parts[1]), Vr(parts[2]), parts[3] == "1");
    }

    public string Serialize() =>
        string.Join(
            '|',
            Sort.ToString(CultureInfo.InvariantCulture),
            MinimumVr?.ToString(CultureInfo.InvariantCulture),
            MaximumVr?.ToString(CultureInfo.InvariantCulture),
            FriendsOnly ? "1" : "0"
        );

    private static int? Vr(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
}
