using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Core.Fulfilment;

public enum ArrService
{
    Sonarr,
    Radarr,
}

public static class ArrDefaults
{
    public const string TagLabel = "wishlistarr";
}

public sealed record ArrConnection(
    Uri Url,
    string ApiKey,
    int? QualityProfileId,
    string? RootFolderPath,
    IReadOnlyList<int> TagIds)
{
    public override string ToString() => $"ArrConnection {{ Url = {this.Url} }}";
}

public sealed record ArrSettingsView(
    string? Url,
    bool HasApiKey,
    int? QualityProfileId,
    string? RootFolderPath,
    IReadOnlyList<int> TagIds);

public sealed record ArrQualityProfile(int Id, string Name);

public sealed record ArrRootFolder(string Path, long FreeSpace);

public sealed record ArrTag(int Id, string Label);

public sealed record ArrTestResult(bool Valid, string Message, string? Version = null);

public sealed record FulfilmentRequest(MediaKind Kind, int TmdbId, IReadOnlyList<int> Seasons);

public enum FulfilmentOutcome
{
    Added,
    Reused,
}

public sealed record FulfilmentResult(FulfilmentOutcome Outcome, int ArrId);

public interface IArrBackend
{
    ArrService Service { get; }

    Task<ArrTestResult> TestAsync(CancellationToken ct = default);

    Task<IReadOnlyList<ArrQualityProfile>> GetQualityProfilesAsync(CancellationToken ct = default);

    Task<IReadOnlyList<ArrRootFolder>> GetRootFoldersAsync(CancellationToken ct = default);

    Task<IReadOnlyList<ArrTag>> GetTagsAsync(CancellationToken ct = default);

    Task<FulfilmentResult> FulfilAsync(FulfilmentRequest request, CancellationToken ct = default);
}

public class ArrException(string message) : Exception(message);

public sealed class ArrNotConfiguredException(string message) : ArrException(message);
