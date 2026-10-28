using WheelWizard.Models.RRInfo;

namespace WheelWizard.RrRooms;

public interface IRoomPresence
{
    bool IsOnline(string friendCode);
    void Replace(IEnumerable<string> onlineFriendCodes);

    /// <summary>The VR and BR a player in a live room has right now (null for a rating the room did not give).</summary>
    bool TryGetRating(string friendCode, out int? vr, out int? br);

    void SetRatings(IEnumerable<RrPlayer> players);
}

public sealed class RoomPresence : IRoomPresence
{
    private HashSet<string> _onlineFriendCodes = new(StringComparer.Ordinal);
    private Dictionary<string, (int? Vr, int? Br)> _ratings = new(StringComparer.Ordinal);

    public bool IsOnline(string friendCode) => Volatile.Read(ref _onlineFriendCodes).Contains(friendCode);

    public void Replace(IEnumerable<string> onlineFriendCodes) =>
        Volatile.Write(
            ref _onlineFriendCodes,
            onlineFriendCodes.Where(code => !string.IsNullOrWhiteSpace(code)).ToHashSet(StringComparer.Ordinal)
        );

    public bool TryGetRating(string friendCode, out int? vr, out int? br)
    {
        if (Volatile.Read(ref _ratings).TryGetValue(friendCode, out var rating))
        {
            (vr, br) = rating;
            return true;
        }

        vr = br = null;
        return false;
    }

    public void SetRatings(IEnumerable<RrPlayer> players) =>
        Volatile.Write(
            ref _ratings,
            players
                .Where(player => !string.IsNullOrWhiteSpace(player.FriendCode))
                .GroupBy(player => player.FriendCode, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => (group.First().Vr, group.First().Br), StringComparer.Ordinal)
        );
}
