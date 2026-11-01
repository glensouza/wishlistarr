namespace Wishlistarr.Core.Library;

public interface IWatchIdentityResolver
{
    Task<bool> MatchesAsync(string serverId, string itemKey, string eventGuid, string titleKey, string titleGuid, bool episode, CancellationToken ct = default);
}
