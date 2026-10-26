using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wishlistarr.Core.Content;
using Wishlistarr.Core.Kids;
using Wishlistarr.Data;

namespace Wishlistarr.Api.Kids;

public sealed class KidProfiles(
    WishlistarrDbContext db,
    IPasswordHasher<KidProfile> hasher,
    IOptions<KidOptions> options,
    TimeProvider time)
{
    public const int MaxNameLength = 50;
    public const int MaxAvatarLength = 32;
    public const int MaxAgeLimit = 21;
    public const int MaxWeeklyAllowance = 1000;
    public const int MaxPlexUserIdLength = 100;
    public const int MaxBlockedGenres = 40;
    public const int MaxBlockedKeywords = 100;
    public const int MaxAllowedLanguages = 50;

    public async Task<IReadOnlyList<KidProfile>> ListAsync(CancellationToken ct = default) =>
        await db.KidProfiles.AsNoTracking().Where(profile => profile.ArchivedAt == null).OrderBy(profile => profile.Id).ToListAsync(ct);

    public async Task<ProfileOutcome> CreateAsync(KidProfileRequest request, CancellationToken ct = default)
    {
        if (Validate(request, out string? error) is not ValidatedProfile input)
        {
            return ProfileOutcome.Invalid(error ?? "Invalid profile.");
        }

        KidProfile profile = new()
        {
            Name = input.Name,
            Avatar = input.Avatar,
            PlexLabel = input.PlexLabel,
            CreatedAt = time.GetUtcNow(),
        };
        Apply(profile, input);
        if (input.Pin is not null)
        {
            profile.PinHash = hasher.HashPassword(profile, input.Pin);
        }

        return await this.SaveAsync(profile, isNew: true, ct);
    }

    public async Task<ProfileOutcome> UpdateAsync(int id, KidProfileRequest request, CancellationToken ct = default)
    {
        if (Validate(request, out string? error) is not ValidatedProfile input)
        {
            return ProfileOutcome.Invalid(error ?? "Invalid profile.");
        }

        KidProfile? profile = await this.FindActiveAsync(id, ct);
        if (profile is null)
        {
            return ProfileOutcome.NotFound();
        }

        Apply(profile, input);
        if (input.Pin is not null)
        {
            profile.PinHash = hasher.HashPassword(profile, input.Pin);
        }
        else if (request.RemovePin)
        {
            profile.PinHash = null;
        }

        return await this.SaveAsync(profile, isNew: false, ct);
    }

    public async Task<bool> ArchiveAsync(int id, bool removeOwnedLabels, CancellationToken ct = default)
    {
        KidProfile? profile = await this.FindActiveAsync(id, ct);
        if (profile is null)
        {
            return false;
        }

        profile.ArchivedAt = time.GetUtcNow();
        profile.RemoveOwnedLabelsOnArchive = removeOwnedLabels;
        profile.PinHash = null;
        profile.PlexUserId = null;
        profile.LockedAt = null;
        profile.FailedPinCount = 0;
        db.KidSessions.RemoveRange(await db.KidSessions.Where(session => session.KidProfileId == id).ToListAsync(ct));
        db.KidPinAttempts.RemoveRange(await db.KidPinAttempts.Where(attempt => attempt.KidProfileId == id).ToListAsync(ct));
        db.KidDeviceProfiles.RemoveRange(await db.KidDeviceProfiles.Where(assignment => assignment.KidProfileId == id).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyDictionary<int, DateTimeOffset>> ListDeviceLocksAsync(CancellationToken ct = default) =>
        (await db.KidPinAttempts.AsNoTracking().Where(attempt => attempt.LockedAt != null).ToListAsync(ct))
            .GroupBy(attempt => attempt.KidProfileId)
            .ToDictionary(group => group.Key, group => group.Min(attempt => attempt.LockedAt!.Value));

    public async Task<bool> UnlockAsync(int id, CancellationToken ct = default)
    {
        KidProfile? profile = await this.FindActiveAsync(id, ct);
        if (profile is null)
        {
            return false;
        }

        profile.LockedAt = null;
        profile.FailedPinCount = 0;
        profile.LastFailedPinAt = null;
        db.KidPinAttempts.RemoveRange(await db.KidPinAttempts.Where(attempt => attempt.KidProfileId == id).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static void Apply(KidProfile profile, ValidatedProfile input)
    {
        profile.Name = input.Name;
        profile.Avatar = input.Avatar;
        profile.PlexLabel = input.PlexLabel;
        profile.MovieAgeLimit = input.MovieAgeLimit;
        profile.TvAgeLimit = input.TvAgeLimit;
        profile.PlexUserId = input.PlexUserId;
        profile.WeeklyAllowance = input.WeeklyAllowance;
        profile.AutoApprove = input.AutoApprove;
        profile.AutoApproveMovieRating = input.AutoApproveMovieRating;
        profile.AutoApproveTvRating = input.AutoApproveTvRating;
        profile.BlockedGenreIds = [.. input.BlockedGenreIds];
        profile.BlockedKeywordIds = [.. input.BlockedKeywordIds];
        profile.AllowedOriginalLanguages = [.. input.AllowedOriginalLanguages];
    }

    private static ValidatedProfile? Validate(KidProfileRequest request, out string? error)
    {
        error = null;
        string name = request.Name?.Trim() ?? string.Empty;
        string avatar = request.Avatar?.Trim() ?? string.Empty;
        string label = string.IsNullOrWhiteSpace(request.PlexLabel) ? name : request.PlexLabel.Trim();
        string? plexUserId = string.IsNullOrWhiteSpace(request.PlexUserId) ? null : request.PlexUserId.Trim();
        IReadOnlyList<int> genres = [];
        IReadOnlyList<int> keywords = [];
        IReadOnlyList<string> languages = [];
        RatingLevel? movieRating = null;
        RatingLevel? tvRating = null;

        if (name.Length is 0 or > MaxNameLength)
        {
            error = $"Names must be 1 to {MaxNameLength} characters.";
        }
        else if (avatar.Length is 0 or > MaxAvatarLength
            || !avatar.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-'))
        {
            error = $"Avatars are 1 to {MaxAvatarLength} lowercase letters, digits or dashes.";
        }
        else if (PlexLabelRules.Validate(label) is string labelError)
        {
            error = labelError;
        }
        else if (request.Pin is not null && !KidPins.IsValid(request.Pin))
        {
            error = $"PINs are {KidPins.MinLength} to {KidPins.MaxLength} digits.";
        }
        else if (request.MovieAgeLimit is < 0 or > MaxAgeLimit || request.TvAgeLimit is < 0 or > MaxAgeLimit)
        {
            error = $"Age limits are 0 to {MaxAgeLimit}.";
        }
        else if (request.WeeklyAllowance is < 0 or > MaxWeeklyAllowance)
        {
            error = $"Weekly allowances are 0 to {MaxWeeklyAllowance}.";
        }
        else if (plexUserId?.Length > MaxPlexUserIdLength)
        {
            error = $"Plex user ids are limited to {MaxPlexUserIdLength} characters.";
        }

        if (error is null)
        {
            TryNormalizeIds(request.BlockedGenreIds, MaxBlockedGenres, "Blocked genres", out genres, out error);
        }

        if (error is null)
        {
            TryNormalizeIds(request.BlockedKeywordIds, MaxBlockedKeywords, "Blocked keywords", out keywords, out error);
        }

        if (error is null)
        {
            TryNormalizeLanguages(request.AllowedOriginalLanguages, out languages, out error);
        }

        if (error is null)
        {
            TryRating(request.AutoApproveMovieRating, out movieRating, out error);
        }

        if (error is null)
        {
            TryRating(request.AutoApproveTvRating, out tvRating, out error);
        }

        return error is null
            ? new ValidatedProfile(
                name,
                avatar,
                label,
                request.Pin,
                request.MovieAgeLimit,
                request.TvAgeLimit,
                plexUserId,
                request.WeeklyAllowance,
                request.AutoApprove,
                movieRating,
                tvRating,
                genres,
                keywords,
                languages)
            : null;
    }

    private async Task<KidProfile?> FindActiveAsync(int id, CancellationToken ct) =>
        await db.KidProfiles.FirstOrDefaultAsync(row => row.Id == id && row.ArchivedAt == null, ct);

    private async Task<ProfileOutcome> SaveAsync(KidProfile profile, bool isNew, CancellationToken ct)
    {
        List<KidProfile> others = await db.KidProfiles
            .Where(row => row.ArchivedAt == null && row.Id != profile.Id)
            .ToListAsync(ct);
        if (others.Any(row => string.Equals(row.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return ProfileOutcome.Conflict("A profile with that name already exists.");
        }

        if (others.Any(row => string.Equals(row.PlexLabel, profile.PlexLabel, StringComparison.OrdinalIgnoreCase)))
        {
            return ProfileOutcome.Conflict("Another profile already uses that Plex label.");
        }

        if (options.Value.RequirePinWhenLimitsDiffer)
        {
            List<KidProfile> all = [.. others, profile];
            bool limitsDiffer = all.Select(row => (row.MovieAgeLimit, row.TvAgeLimit)).Distinct().Count() > 1;
            string[] missing = [.. all.Where(row => row.PinHash is null).Select(row => row.Name)];
            if (limitsDiffer && missing.Length > 0)
            {
                return ProfileOutcome.Invalid(
                    "A PIN is required while profiles have different limits. Set a PIN for: " + string.Join(", ", missing) + ".");
            }
        }

        if (isNew)
        {
            db.KidProfiles.Add(profile);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return ProfileOutcome.Conflict("A profile with that name or Plex label already exists.");
        }

        return ProfileOutcome.Ok(profile);
    }

    private sealed record ValidatedProfile(
        string Name,
        string Avatar,
        string PlexLabel,
        string? Pin,
        int? MovieAgeLimit,
        int? TvAgeLimit,
        string? PlexUserId,
        int? WeeklyAllowance,
        bool AutoApprove,
        RatingLevel? AutoApproveMovieRating,
        RatingLevel? AutoApproveTvRating,
        IReadOnlyList<int> BlockedGenreIds,
        IReadOnlyList<int> BlockedKeywordIds,
        IReadOnlyList<string> AllowedOriginalLanguages);

    private static bool TryRating(string? value, out RatingLevel? rating, out string? error)
    {
        rating = null;
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        string text = value.Trim();
        if (text.All(char.IsAsciiDigit)
            || !Enum.TryParse(text, ignoreCase: true, out RatingLevel parsed)
            || !Enum.IsDefined(parsed))
        {
            error = "Auto-approve ratings must be a rating level: preschool, general, children, parentalGuidance, teen, mature or adult.";
            return false;
        }

        rating = parsed;
        return true;
    }

    private static void TryNormalizeIds(
        IReadOnlyList<int>? ids,
        int max,
        string label,
        out IReadOnlyList<int> normalized,
        out string? error)
    {
        normalized = [];
        error = null;
        if (ids is null || ids.Count == 0)
        {
            return;
        }

        if (ids.Count > max)
        {
            error = $"{label} are limited to {max}.";
            return;
        }

        List<int> distinct = [];
        foreach (int id in ids)
        {
            if (id <= 0)
            {
                error = $"{label} must be positive TMDB ids.";
                return;
            }

            if (!distinct.Contains(id))
            {
                distinct.Add(id);
            }
        }

        normalized = distinct;
    }

    private static void TryNormalizeLanguages(
        IReadOnlyList<string>? languages,
        out IReadOnlyList<string> normalized,
        out string? error)
    {
        normalized = [];
        error = null;
        if (languages is null || languages.Count == 0)
        {
            return;
        }

        if (languages.Count > MaxAllowedLanguages)
        {
            error = $"Allowed languages are limited to {MaxAllowedLanguages}.";
            return;
        }

        List<string> distinct = [];
        foreach (string? language in languages)
        {
            string? code = ContentLanguages.Canonical(language);
            if (code is null)
            {
                error = "Allowed languages must be ISO 639-1 codes.";
                return;
            }

            if (!distinct.Contains(code))
            {
                distinct.Add(code);
            }
        }

        normalized = distinct;
    }
}

public sealed record KidProfileRequest(
    string? Name,
    string? Avatar,
    string? Pin,
    bool RemovePin,
    int? MovieAgeLimit,
    int? TvAgeLimit,
    string? PlexLabel,
    string? PlexUserId,
    int? WeeklyAllowance,
    bool AutoApprove = false,
    string? AutoApproveMovieRating = null,
    string? AutoApproveTvRating = null,
    IReadOnlyList<int>? BlockedGenreIds = null,
    IReadOnlyList<int>? BlockedKeywordIds = null,
    IReadOnlyList<string>? AllowedOriginalLanguages = null);

public sealed record ProfileOutcome(KidProfile? Profile, int Status, string? Error)
{
    public static ProfileOutcome Ok(KidProfile profile) => new(profile, StatusCodes.Status200OK, null);

    public static ProfileOutcome Invalid(string error) => new(null, StatusCodes.Status400BadRequest, error);

    public static ProfileOutcome Conflict(string error) => new(null, StatusCodes.Status409Conflict, error);

    public static ProfileOutcome NotFound() => new(null, StatusCodes.Status404NotFound, "Profile not found.");
}
