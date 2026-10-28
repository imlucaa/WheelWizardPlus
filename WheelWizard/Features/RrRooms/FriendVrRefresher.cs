using WheelWizard.Shared.Services;

namespace WheelWizard.RrRooms;

/// <summary>Looks up the VR of friends who are not in a room, from their rwfc.net profiles.</summary>
public interface IFriendVrRefresher
{
    Task RefreshAsync(IEnumerable<string> friendCodes, CancellationToken cancellationToken);
}

/// <summary>
/// A few friends are looked up on every pass, and each one at most every ten minutes (a friend rwfc.net does not know is
/// not asked again for that time either), so a long friends list is filled in over a few passes without a burst of requests.
/// </summary>
public sealed class FriendVrRefresher(IApiCaller<IRwfcApi> api, IRoomPresence presence, TimeProvider time) : IFriendVrRefresher
{
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(10);
    private const int PerPass = 6;
    private const int Parallel = 3;

    private readonly Dictionary<string, DateTimeOffset> _asked = new(StringComparer.Ordinal);

    public async Task RefreshAsync(IEnumerable<string> friendCodes, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var due = friendCodes
            .Where(code => !string.IsNullOrWhiteSpace(code) && code != "0000-0000-0000" && !presence.IsOnline(code))
            .Distinct(StringComparer.Ordinal)
            .Where(code => !_asked.TryGetValue(code, out var when) || now - when > Stale)
            .OrderBy(code => _asked.GetValueOrDefault(code))
            .Take(PerPass)
            .ToList();
        foreach (var code in due)
            _asked[code] = now;

        using var gate = new SemaphoreSlim(Parallel);
        await Task.WhenAll(due.Select(code => LookUpAsync(code, gate, cancellationToken)));
    }

    private async Task LookUpAsync(string friendCode, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var result = await api.CallApiAsync(rwfcApi => rwfcApi.GetPlayerProfileAsync(friendCode));
            if (result.IsSuccess && result.Value is { Vr: > 0 } profile)
                presence.SetProfileVr(friendCode, profile.Vr);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A friend that cannot be looked up keeps the VR the save has.
        }
        finally
        {
            gate.Release();
        }
    }
}
