using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Wishlistarr.Api.Labels;
using Wishlistarr.Api.Requests;
using Wishlistarr.Api.Search;
using Wishlistarr.Core.Content;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Tmdb;

namespace Wishlistarr.Api.Kids;

public static class ContentControlsEndpoints
{
    public static void MapContentControls(this RouteGroupBuilder profiles)
    {
        profiles.MapGet("/title-search", async (string? q, int? page, ITmdbClient tmdb, CancellationToken ct) =>
        {
            if (!KidSearch.TryNormalize(q, out string query) || page is < 1 or > 10)
            {
                return Invalid("Enter 2 to 100 characters and a page from 1 to 10.");
            }

            try
            {
                TmdbPage<TmdbSearchResult> found = await tmdb.SearchAsync(query, page: page ?? 1, ct: ct);
                return Results.Ok(found.Results.Select(KidContentPipeline.ToCandidate).OfType<KidCandidate>().DistinctBy(row => row.Key)
                    .Select(row => new TitleRuleInput(row.Key.Kind, row.Key.TmdbId, row.Title, true)));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is TmdbException or HttpRequestException or TaskCanceledException)
            {
                return Unavailable();
            }
        });

        profiles.MapGet("/{id}/titles", async (int id, bool? blocked, string? q, int? limit, int? offset, WishlistarrDbContext db, CancellationToken ct) =>
        {
            if (id <= 0 || q?.Length > 100 || limit is < 1 or > 200 || offset is < 0)
            {
                return Invalid("Invalid profile, search or page.");
            }

            if (!await Active(db, id, ct)) return Missing();
            IQueryable<ProfileTitleRule> rows = db.ProfileTitleRules.AsNoTracking().Where(row => row.KidProfileId == id);
            if (blocked.HasValue) rows = rows.Where(row => row.Blocked == blocked.Value);
            if (!string.IsNullOrWhiteSpace(q))
            {
                string query = q.Trim().ToLower();
                rows = rows.Where(row => (row.Title ?? (row.Kind == MediaKind.Movie ? "Movie " : "Show ") + row.TmdbId).ToLower().Contains(query) || row.TmdbId.ToString().Contains(query));
            }

            return Results.Ok(new { total = await rows.CountAsync(ct), items = await rows.OrderBy(row => row.Title).ThenBy(row => row.Kind).ThenBy(row => row.TmdbId).ThenBy(row => row.Blocked).Skip(offset ?? 0).Take(limit ?? 50).Select(row => new TitleRuleInput(row.Kind, row.TmdbId, row.Title ?? (row.Kind == MediaKind.Movie ? "Movie " : "Show ") + row.TmdbId, row.Blocked)).ToListAsync(ct) });
        });

        profiles.MapPost("/{id}/titles", async (int id, TitleRuleInput? input, WishlistarrDbContext db, CancellationToken ct) =>
        {
            if (id <= 0 || !ValidTitle(input)) return Invalid("A movie or show, positive TMDB id and title of 1 to 300 characters are required.");
            if (!await Active(db, id, ct)) return Missing();
            if (await db.ProfileTitleRules.AnyAsync(row => row.KidProfileId == id && row.Kind == input!.Kind && row.TmdbId == input.TmdbId && row.Blocked == input.Blocked, ct))
                return Results.Problem(statusCode: 409, title: "This title is already on the list.");
            if (await db.ProfileTitleRules.CountAsync(row => row.KidProfileId == id, ct) >= 1000)
                return Invalid("A profile can have at most 1000 title entries.");
            db.ProfileTitleRules.Add(ToEntity(id, input!));
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                return Results.Problem(statusCode: 409, title: "This title is already on the list.");
            }

            return Results.NoContent();
        });

        profiles.MapDelete("/{id}/titles/{kind}/{tmdbId}", async (int id, string kind, int tmdbId, bool blocked, WishlistarrDbContext db, CancellationToken ct) =>
        {
            if (id <= 0 || tmdbId <= 0 || kind is not ("movie" or "show")) return Invalid("Invalid title key.");
            MediaKind mediaKind = kind == "movie" ? MediaKind.Movie : MediaKind.Show;
            if (!await Active(db, id, ct)) return Missing();
            int removed = await db.ProfileTitleRules.Where(row => row.KidProfileId == id && row.Kind == mediaKind && row.TmdbId == tmdbId && row.Blocked == blocked).ExecuteDeleteAsync(ct);
            return removed == 0 ? Missing() : Results.NoContent();
        });

        profiles.MapPost("/{id}/preview", async (int id, KidRequestBody? input, WishlistarrDbContext db, KidRequestFlow flow, CancellationToken ct) =>
        {
            if (id <= 0 || input?.Kind is not MediaKind kind || !ValidKind(kind) || input.TmdbId is not > 0) return Invalid("Invalid title key.");
            KidProfile? profile = await db.KidProfiles.AsNoTracking().FirstWithContentRulesAsync(row => row.Id == id && row.ArchivedAt == null, ct);
            if (profile is null) return Missing();
            try
            {
                PolicyEvaluation? evaluation = await flow.EvaluateAsync(profile, new TitleKey(kind, input.TmdbId.Value), ct);
                return evaluation is null ? Missing() : Results.Ok(new { decision = evaluation.Decision.Outcome, reason = Explain(evaluation.Decision.Reason), reasonCode = evaluation.Decision.Reason.Code, reasonDetails = evaluation.Decision.Reason });
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is TmdbException or HttpRequestException or TaskCanceledException)
            {
                return Unavailable();
            }
        });

        profiles.MapGet("/{id}/content-rules", async (int id, WishlistarrDbContext db, CancellationToken ct) =>
        {
            KidProfile? profile = await db.KidProfiles.AsNoTracking().FirstWithContentRulesAsync(row => row.Id == id && row.ArchivedAt == null, ct);
            return profile is null ? Missing() : Results.Ok(new
            {
                version = 1,
                rules = new
                {
                    profile.MovieAgeLimit,
                    profile.TvAgeLimit,
                    profile.WeeklyAllowance,
                    profile.AutoApprove,
                    profile.AutoApproveMovieRating,
                    profile.AutoApproveTvRating,
                    profile.BlockedGenreIds,
                    profile.BlockedKeywordIds,
                    profile.AllowedOriginalLanguages,
                },
                titles = profile.TitleRules.Select(row => new TitleRuleInput(row.Kind, row.TmdbId, row.Title ?? $"{row.Kind} {row.TmdbId}", row.Blocked))
            });
        });

        profiles.MapPut("/{id}/content-rules", ImportAsync);
    }

    private static async Task<IResult> ImportAsync(int id, HttpRequest request, WishlistarrDbContext db, KidProfiles profiles, IProfileLabelGate labels, CancellationToken ct)
    {
        const int maxBytes = 1048576;
        using MemoryStream body = new();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (body.Length + read > maxBytes) return Results.StatusCode(413);
            await body.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        body.Position = 0;
        JsonDocument document;
        try { document = await JsonDocument.ParseAsync(body, cancellationToken: ct); }
        catch (JsonException) { return Invalid("Invalid content rules file."); }
        using JsonDocument parsed = document;
        JsonElement file = parsed.RootElement;
        string[] keys = ["movieAgeLimit", "tvAgeLimit", "weeklyAllowance", "autoApprove", "autoApproveMovieRating", "autoApproveTvRating", "blockedGenreIds", "blockedKeywordIds", "allowedOriginalLanguages"];
        if (id <= 0 || !ExactKeys(file, ["version", "rules", "titles"]) || (file.GetProperty("version").ValueKind != JsonValueKind.Number || !file.GetProperty("version").TryGetInt32(out int version)) || version != 1
            || !ExactKeys(file.GetProperty("rules"), keys) || file.GetProperty("titles").ValueKind != JsonValueKind.Array || file.GetProperty("titles").GetArrayLength() > 1000)
        {
            return Invalid("Invalid content rules file. All rules and title lists are required.");
        }

        JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
        options.Converters.Add(new global::System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        KidProfileRequest? rules;
        List<TitleRuleInput>? titles;
        try
        {
            rules = file.GetProperty("rules").Deserialize<KidProfileRequest>(options);
            titles = file.GetProperty("titles").Deserialize<List<TitleRuleInput>>(options);
        }
        catch (JsonException)
        {
            return Invalid("Invalid content rules file.");
        }

        if (rules is null || titles is null || titles.Any(row => !ValidTitle(row)) || titles.DistinctBy(row => (row.Kind, row.TmdbId, row.Blocked)).Count() != titles.Count
            || file.GetProperty("titles").EnumerateArray().Any(row => !ExactKeys(row, ["kind", "tmdbId", "title", "blocked"]))
            || keys.Any(key => file.GetProperty("rules").GetProperty(key).ValueKind == JsonValueKind.Null && key is not ("movieAgeLimit" or "tvAgeLimit" or "weeklyAllowance" or "autoApproveMovieRating" or "autoApproveTvRating")))
        {
            return Invalid("Invalid content rules file.");
        }

        // Acquire the label gate before SQLite's write transaction; taking them in the opposite order could block undo's ledger cleanup.
        using IDisposable held = await labels.EnterAsync(ct);
        KidProfile? profile = await db.KidProfiles.FirstWithContentRulesAsync(row => row.Id == id && row.ArchivedAt == null, ct);
        if (profile is null) return Missing();
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(ct);
        ProfileOutcome result = await profiles.UpdateWithLabelGateHeldAsync(id, rules with { Name = profile.Name, Avatar = profile.Avatar, PlexLabel = profile.PlexLabel, PlexUserId = profile.PlexUserId, HomeRows = profile.HomeRows }, ct);
        if (result.Profile is null) return Results.Problem(statusCode: result.Status, title: result.Error);
        db.ProfileTitleRules.RemoveRange(profile.TitleRules);
        await db.SaveChangesAsync(ct);
        db.ProfileTitleRules.AddRange(titles.Select(row => ToEntity(id, row)));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(KidProfileSummary.From(result.Profile));
    }

    private static bool ExactKeys(JsonElement value, string[] keys) => value.ValueKind == JsonValueKind.Object
        && value.EnumerateObject().Count() == keys.Length && keys.All(key => value.TryGetProperty(key, out _));

    private static bool ValidKind(MediaKind kind) => kind is MediaKind.Movie or MediaKind.Show;

    private static bool ValidTitle(TitleRuleInput? row) => row is not null && ValidKind(row.Kind) && row.TmdbId > 0
        && !string.IsNullOrWhiteSpace(row.Title) && row.Title.Length <= 300 && !row.Title.Any(char.IsControl);

    private static ProfileTitleRule ToEntity(int id, TitleRuleInput row) => new() { KidProfileId = id, Kind = row.Kind, TmdbId = row.TmdbId, Title = row.Title.Trim() == $"{row.Kind} {row.TmdbId}" ? null : row.Title.Trim(), TitleFetchedAt = null, Blocked = row.Blocked };

    private static Task<bool> Active(WishlistarrDbContext db, int id, CancellationToken ct) => db.KidProfiles.AnyAsync(row => row.Id == id && row.ArchivedAt == null, ct);

    private static IResult Missing() => Results.Problem(statusCode: 404, title: "Profile or title not found.");

    private static IResult Invalid(string title) => Results.Problem(statusCode: 400, title: title);

    private static IResult Unavailable() => Results.Problem(statusCode: 502, title: "TMDB could not be reached.");

    private static string Explain(ContentReason reason) => reason.Code switch
    {
        ContentReasonCode.ExplicitlyBlocked => "This title is on the profile's blocked list.",
        ContentReasonCode.ExplicitlyAllowed => "This title is on the profile's always-allowed list.",
        ContentReasonCode.AboveRatingLimit => $"The rating {reason.Rating} exceeds the profile's age limit.",
        ContentReasonCode.Unrated => "This title has no recognized rating and needs a parent's approval.",
        ContentReasonCode.BlockedGenre => $"This title includes blocked genre {reason.GenreId}.",
        ContentReasonCode.BlockedKeyword => $"This title includes blocked keyword {reason.KeywordId}.",
        ContentReasonCode.UnknownGenres => "Genre information is missing and needs a parent's approval.",
        ContentReasonCode.LanguageNotAllowed => "The original language is not on the profile's allowed language list.",
        ContentReasonCode.PreviouslyRevoked => "This title was previously revoked and needs a parent to review it again.",
        ContentReasonCode.MetadataUnavailable => "Required title information could not be checked or is unsuitable. Try another title or try again later.",
        _ => "This title meets the profile's content rules.",
    };
}

public sealed record TitleRuleInput(MediaKind Kind, int TmdbId, string Title, bool Blocked);
