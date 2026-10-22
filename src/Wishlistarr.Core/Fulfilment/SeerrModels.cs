using System.Net;
using Wishlistarr.Core.MediaServers;

namespace Wishlistarr.Core.Fulfilment;

public static class SeerrPermissions
{
    public const long Admin = 2;
    public const long ManageRequests = 16;
    public const long Request = 32;
    public const long AutoApprove = 128;
    public const long AutoApproveMovie = 256;
    public const long AutoApproveTv = 512;
    public const long RequestMovie = 262144;
    public const long RequestTv = 524288;

    public static bool CanRequest(long permissions, MediaKind kind) =>
        HasAny(permissions, Request | (kind == MediaKind.Movie ? RequestMovie : RequestTv));

    public static bool AutoApproves(long permissions, MediaKind kind) =>
        HasAny(permissions, AutoApprove | ManageRequests | (kind == MediaKind.Movie ? AutoApproveMovie : AutoApproveTv));

    private static bool HasAny(long permissions, long wanted) => (permissions & (Admin | wanted)) != 0;
}

public sealed record SeerrConnection(Uri Url, string ApiKey, int UserId)
{
    public override string ToString() => $"SeerrConnection {{ Url = {this.Url}, UserId = {this.UserId} }}";
}

public sealed record SeerrSettingsView(string? Url, bool HasApiKey, int? UserId);

public enum SeerrServerKind
{
    Seerr,
    Overseerr,
}

public sealed record SeerrServerFlavour(
    SeerrServerKind Kind,
    bool SettingsRead,
    bool SpecialsRequestable,
    bool AllIncludesSpecials,
    bool AllSkipsEmptySeasons);

public sealed record SeerrTestResult(string Version, IReadOnlyList<string> Warnings, SeerrServerFlavour Flavour);

public enum SeerrRequestStatus
{
    Unknown = 0,
    Pending = 1,
    Approved = 2,
    Declined = 3,
    Failed = 4,
    Completed = 5,
}

public enum SeerrAvailability
{
    Unknown = 1,
    Pending = 2,
    Processing = 3,
    PartiallyAvailable = 4,
    Available = 5,
    Blocklisted = 6,
    Deleted = 7,
}

public sealed record SeerrSubmitRequest(MediaKind Kind, int TmdbId, IReadOnlyList<int>? Seasons);

public enum SeerrSubmitOutcome
{
    Created,
    Attached,
    AlreadyAvailable,
    AlreadyCovered,
}

public sealed record SeerrSubmission(
    SeerrSubmitOutcome Outcome,
    int? Id,
    SeerrRequestStatus? Status,
    SeerrAvailability Availability,
    SeerrAvailability? SeasonsAvailability,
    bool Attached);

public sealed record SeerrTrackedRequest(int Id, SeerrRequestStatus Status, SeerrAvailability Availability, SeerrAvailability? SeasonsAvailability);

public interface ISeerrBackend
{
    Task<SeerrTestResult> TestAsync(CancellationToken ct = default);

    Task<SeerrSubmission> SubmitAsync(SeerrSubmitRequest request, CancellationToken ct = default);

    Task<SeerrTrackedRequest> GetRequestAsync(int requestId, CancellationToken ct = default);
}

public class SeerrException(string message, HttpStatusCode? statusCode = null) : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

public sealed class SeerrNotConfiguredException(string message) : SeerrException(message);
