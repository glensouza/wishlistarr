using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Api.Requests;
using Wishlistarr.Core.Content;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;

namespace Wishlistarr.Tests.Requests;

public sealed class RequestQueueTests
{
    private const string QueuePath = "/api/v1/requests/queue";
    private const string OfficialTrailer = "M7lc1UVf-VE";

    [Fact]
    public async Task Pending_queue_includes_synopsis_rating_providers_trailer_and_the_ceiling_reason()
    {
        using KidRequestApiFactory factory = new();
        string overview = new('A', 2001);
        factory.Tmdb.SetBody("/3/movie/703", $$"""
            {"id":703,"title":"Movie 703","overview":"{{overview}}","genres":[{"id":28,"name":"Action"}],"original_language":"en","vote_average":7}
            """);
        factory.Tmdb.SetBody("/3/movie/703/release_dates", """
            {"results":[{"iso_3166_1":"US","release_dates":[{"certification":"PG-13","descriptors":["Violence"," Violence ","Violence"],"type":3}]}]}
            """);
        factory.Tmdb.SetBody("/3/movie/703/watch/providers", """
            {"results":{
              "DE":{"flatrate":[{"provider_id":1,"provider_name":"German Stream","logo_path":"/de.jpg"}]},
              "US":{
                "link":"https://www.justwatch.com/us/movie/the-matrix",
                "flatrate":[
                  {"provider_id":8,"provider_name":"Stream","logo_path":"/s.jpg"},
                  {"provider_id":9,"provider_name":"  "},
                  {"provider_id":8,"provider_name":"stream"}],
                "rent":[{"provider_id":2,"provider_name":"RentCo","logo_path":"/r.jpg"}]}}}
            """);
        (HttpClient kid, int profileId) = await factory.SignInKidAsync(movieAgeLimit: 13, autoApproveMovieRating: "parentalGuidance");
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        int id = await AskAsync(kid, 703);

        HttpResponseMessage response = await parent.GetAsync(QueuePath);
        string raw = await response.Content.ReadAsStringAsync();
        JsonElement body = JsonDocument.Parse(raw).RootElement;
        JsonElement item = Assert.Single(body.GetProperty("items").EnumerateArray());
        JsonElement reason = item.GetProperty("reason");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("private", Header(response, "Cache-Control"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no-store", Header(response, "Cache-Control"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(id, item.GetProperty("id").GetInt32());
        Assert.Equal(profileId, item.GetProperty("kidProfileId").GetInt32());
        Assert.Equal("Ada", item.GetProperty("kidName").GetString());
        Assert.Equal("movie", item.GetProperty("kind").GetString());
        Assert.Equal(703, item.GetProperty("tmdbId").GetInt32());
        Assert.Equal("Movie 703", item.GetProperty("title").GetString());
        Assert.Equal(2000, item.GetProperty("overview").GetString()!.Length);
        Assert.Equal(overview[..2000], item.GetProperty("overview").GetString());
        Assert.Equal("PG-13", item.GetProperty("rating").GetString());
        Assert.Equal(["Violence"], Texts(item.GetProperty("descriptors")));
        Assert.Equal(["Stream"], item.GetProperty("providers").EnumerateArray().Select(provider => provider.GetProperty("name").GetString()));
        Assert.Equal(JsonValueKind.Null, item.GetProperty("trailerKey").ValueKind);
        Assert.Empty(item.GetProperty("gaps").EnumerateArray());
        Assert.Equal("aboveCeiling", reason.GetProperty("code").GetString());
        Assert.Equal("PG-13", reason.GetProperty("rating").GetString());
        Assert.Equal("teen", reason.GetProperty("level").GetString());
        Assert.Equal("parentalGuidance", reason.GetProperty("maxLevel").GetString());
        Assert.Equal(JsonValueKind.Null, reason.GetProperty("genreName").ValueKind);
        Assert.Equal(JsonValueKind.Null, reason.GetProperty("keywordName").ValueKind);
        Assert.Equal(JsonValueKind.Null, reason.GetProperty("language").ValueKind);
        Assert.Equal(1, body.GetProperty("total").GetInt32());
        Assert.Equal(RequestEndpoints.DefaultPage, body.GetProperty("limit").GetInt32());
        Assert.Equal(0, body.GetProperty("offset").GetInt32());
        Assert.DoesNotContain("http", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("justwatch", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("logo_path", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("flatrate", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RentCo", raw);
        Assert.DoesNotContain("German", raw);
        Assert.Equal(0, factory.Tmdb.Count("/3/movie/703/videos"));

        string kidTitle = await (await kid.GetAsync("/api/v1/kid/titles/movie/703")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"providers\"", kidTitle, StringComparison.Ordinal);
        Assert.DoesNotContain("Stream", kidTitle, StringComparison.Ordinal);
        Assert.DoesNotContain("aboveCeiling", kidTitle, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.Forbidden, (await kid.GetAsync(QueuePath)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(QueuePath)).StatusCode);

        factory.Tmdb.SetBody("/3/movie/703/videos", """
            {"results":[
              {"id":"1","key":"short","name":"Too short","site":"YouTube","type":"Trailer","official":true},
              {"id":"2","key":"abcdefghijk","name":"Unofficial","site":"YouTube","type":"Trailer","official":false},
              {"id":"3","key":"M7lc1UVf-VE","name":"Official trailer","site":"YouTube","type":"Trailer","official":true}]}
            """);
        HttpResponseMessage saved = await parent.PutAsJsonAsync("/api/v1/settings/kids.youtube_trailers", new { value = "true" });
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        JsonElement withTrailer = Assert.Single((await QueueAsync(parent)).GetProperty("items").EnumerateArray());
        Assert.Equal(OfficialTrailer, withTrailer.GetProperty("trailerKey").GetString());
        Assert.Empty(withTrailer.GetProperty("gaps").EnumerateArray());
        Assert.Equal(1, factory.Tmdb.Count("/3/movie/703/videos"));
    }

    [Fact]
    public async Task An_unrated_title_says_the_rating_is_missing()
    {
        using KidRequestApiFactory factory = new();
        factory.Tmdb.SetBody("/3/movie/129/watch/providers", """{"results":{}}""");
        (HttpClient kid, _) = await factory.SignInKidAsync();
        await AskAsync(kid, 129);

        JsonElement item = Assert.Single((await QueueAsync(await factory.CreateAuthorizedClientAsync())).GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("rating").ValueKind);
        Assert.Empty(item.GetProperty("descriptors").EnumerateArray());
        Assert.Empty(item.GetProperty("gaps").EnumerateArray());
        Assert.Equal("unrated", item.GetProperty("reason").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Auto_approve_off_omits_rent_and_other_regions()
    {
        using KidRequestApiFactory factory = new();
        factory.Tmdb.SetBody("/3/movie/700", """
            {"id":700,"title":"Movie 700","overview":"A short story.","genres":[{"id":28,"name":"Action"}],"original_language":"en","vote_average":7}
            """);
        factory.Tmdb.SetBody("/3/movie/700/watch/providers", """
            {"results":{
              "DE":{"flatrate":[{"provider_id":3,"provider_name":"German Stream"}]},
              "US":{"flatrate":[{"provider_id":9,"provider_name":"  "}],"rent":[{"provider_id":2,"provider_name":"RentCo"}]}}}
            """);
        (HttpClient kid, _) = await factory.SignInKidAsync();
        await AskAsync(kid, 700);

        HttpResponseMessage response = await (await factory.CreateAuthorizedClientAsync()).GetAsync(QueuePath);
        string raw = await response.Content.ReadAsStringAsync();
        JsonElement item = Assert.Single(JsonDocument.Parse(raw).RootElement.GetProperty("items").EnumerateArray());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("A short story.", item.GetProperty("overview").GetString());
        Assert.Equal("PG", item.GetProperty("rating").GetString());
        Assert.Empty(item.GetProperty("providers").EnumerateArray());
        Assert.Empty(item.GetProperty("gaps").EnumerateArray());
        Assert.Equal("autoApproveOff", item.GetProperty("reason").GetProperty("code").GetString());
        Assert.Equal("PG", item.GetProperty("reason").GetProperty("rating").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("reason").GetProperty("maxLevel").ValueKind);
        Assert.DoesNotContain("RentCo", raw);
        Assert.DoesNotContain("German", raw);
    }

    [Fact]
    public async Task Unknown_genres_are_the_reason_when_the_profile_blocks_genres()
    {
        using KidRequestApiFactory factory = new();
        factory.Tmdb.SetBody("/3/movie/702/watch/providers", """{"results":{"US":{"flatrate":[]}}}""");
        (HttpClient kid, _) = await factory.SignInKidAsync(blockedGenreIds: [28]);
        await AskAsync(kid, 702);

        JsonElement item = Assert.Single((await QueueAsync(await factory.CreateAuthorizedClientAsync())).GetProperty("items").EnumerateArray());
        Assert.Equal("unknownGenres", item.GetProperty("reason").GetProperty("code").GetString());
        Assert.Equal("PG", item.GetProperty("rating").GetString());
        Assert.Empty(item.GetProperty("gaps").EnumerateArray());
    }

    [Fact]
    public async Task A_title_revoked_before_waits_for_that_reason()
    {
        using KidRequestApiFactory factory = new();
        factory.Tmdb.SetBody("/3/movie/700/watch/providers", """
            {"results":{"US":{"flatrate":[{"provider_id":8,"provider_name":"Stream"}]}}}
            """);
        (HttpClient kid, int profileId) = await factory.SignInKidAsync();
        int revoked = await AskAsync(kid, 700);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
            MediaRequest row = await db.MediaRequests.SingleAsync(request => request.Id == revoked);
            row.State = RequestState.Revoked;
            await db.SaveChangesAsync();
        }

        int waiting = await AskAsync(kid, 700);
        JsonElement item = Assert.Single((await QueueAsync(await factory.CreateAuthorizedClientAsync())).GetProperty("items").EnumerateArray());

        Assert.Equal(waiting, item.GetProperty("id").GetInt32());
        Assert.NotEqual(revoked, waiting);
        Assert.Equal(profileId, item.GetProperty("kidProfileId").GetInt32());
        Assert.Equal("revokedBefore", item.GetProperty("reason").GetProperty("code").GetString());
        Assert.Equal("PG", item.GetProperty("reason").GetProperty("rating").GetString());
        Assert.Equal(["Stream"], item.GetProperty("providers").EnumerateArray().Select(provider => provider.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task The_reason_uses_the_current_age_limit()
    {
        using KidRequestApiFactory factory = new();
        (HttpClient kid, int profileId) = await factory.SignInKidAsync(movieAgeLimit: 16);
        await AskAsync(kid, 603);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
            KidProfile profile = await db.KidProfiles.SingleAsync(row => row.Id == profileId);
            profile.MovieAgeLimit = 8;
            await db.SaveChangesAsync();
        }

        JsonElement reason = Assert.Single((await QueueAsync(await factory.CreateAuthorizedClientAsync())).GetProperty("items").EnumerateArray())
            .GetProperty("reason");
        Assert.Equal("aboveRatingLimit", reason.GetProperty("code").GetString());
        Assert.Equal("PG-13", reason.GetProperty("rating").GetString());
        Assert.Equal("teen", reason.GetProperty("level").GetString());
        Assert.Equal("parentalGuidance", reason.GetProperty("maxLevel").GetString());
    }

    [Fact]
    public async Task A_pending_title_the_rule_would_approve_says_it_was_not_taken()
    {
        using KidRequestApiFactory factory = new();
        factory.Tmdb.SetBody("/3/movie/700/watch/providers", """{"results":{}}""");
        (_, int profileId) = await factory.SignInKidAsync(autoApprove: true);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            RequestResult result = await scope.ServiceProvider.GetRequiredService<MediaRequests>().CreateAsync(
                profileId,
                new TitleKey(MediaKind.Movie, 700),
                "Movie 700",
                RequestActor.Kid(profileId, "Ada"));
            Assert.Equal(RequestOutcome.Created, result.Outcome);
        }

        JsonElement item = Assert.Single((await QueueAsync(await factory.CreateAuthorizedClientAsync())).GetProperty("items").EnumerateArray());
        Assert.Equal("notAutoApproved", item.GetProperty("reason").GetProperty("code").GetString());
        Assert.Equal("PG", item.GetProperty("rating").GetString());
        Assert.Equal("Movie 700", item.GetProperty("title").GetString());
        Assert.Empty(item.GetProperty("gaps").EnumerateArray());
    }

    [Fact]
    public async Task Missing_details_leave_the_request_in_the_queue_with_gaps()
    {
        using KidRequestApiFactory factory = new();
        (_, int profileId) = await factory.SignInKidAsync();
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            RequestResult result = await scope.ServiceProvider.GetRequiredService<MediaRequests>().CreateAsync(
                profileId,
                new TitleKey(MediaKind.Movie, 700),
                "Kept Title",
                RequestActor.Kid(profileId, "Ada"));
            Assert.Equal(RequestOutcome.Created, result.Outcome);
        }

        factory.Tmdb.Enqueue("/3/movie/700", StatusCodes.Status502BadGateway);
        JsonElement item = Assert.Single((await QueueAsync(await factory.CreateAuthorizedClientAsync())).GetProperty("items").EnumerateArray());
        string[] gaps = Texts(item.GetProperty("gaps"));

        Assert.Equal("Kept Title", item.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("overview").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("rating").ValueKind);
        Assert.Empty(item.GetProperty("providers").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("trailerKey").ValueKind);
        Assert.Contains("synopsis", gaps);
        Assert.Contains("rating", gaps);
        Assert.Contains("providers", gaps);
        Assert.DoesNotContain("trailer", gaps);
        Assert.Equal("unavailable", item.GetProperty("reason").GetProperty("code").GetString());
        Assert.Equal(1, factory.Tmdb.Count("/3/movie/700"));
        Assert.Equal(0, factory.Tmdb.Count("/3/movie/700/release_dates"));
        Assert.Equal(0, factory.Tmdb.Count("/3/movie/700/watch/providers"));
        Assert.Equal(0, factory.Tmdb.Count("/3/movie/700/videos"));
    }

    [Fact]
    public async Task A_provider_failure_keeps_the_synopsis_and_rating()
    {
        using KidRequestApiFactory factory = new();
        factory.Tmdb.SetBody("/3/movie/700", """
            {"id":700,"title":"Movie 700","overview":"A short story.","genres":[{"id":28,"name":"Action"}],"original_language":"en","vote_average":7}
            """);
        (HttpClient kid, _) = await factory.SignInKidAsync();
        await AskAsync(kid, 700);
        factory.Tmdb.Enqueue("/3/movie/700/watch/providers", StatusCodes.Status500InternalServerError);

        JsonElement item = Assert.Single((await QueueAsync(await factory.CreateAuthorizedClientAsync())).GetProperty("items").EnumerateArray());
        Assert.Equal("A short story.", item.GetProperty("overview").GetString());
        Assert.Equal("PG", item.GetProperty("rating").GetString());
        Assert.Empty(item.GetProperty("providers").EnumerateArray());
        Assert.Equal(["providers"], Texts(item.GetProperty("gaps")));
        Assert.Equal("autoApproveOff", item.GetProperty("reason").GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_certification_failure_keeps_the_synopsis_and_names_the_rating_gap()
    {
        using KidRequestApiFactory factory = new();
        factory.Tmdb.SetBody("/3/movie/700", """
            {"id":700,"title":"Movie 700","overview":"A short story.","genres":[{"id":28,"name":"Action"}],"original_language":"en","vote_average":7}
            """);
        factory.Tmdb.SetBody("/3/movie/700/watch/providers", """
            {"results":{"US":{"flatrate":[{"provider_id":8,"provider_name":"Stream"}]}}}
            """);
        (_, int profileId) = await factory.SignInKidAsync();
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            RequestResult result = await scope.ServiceProvider.GetRequiredService<MediaRequests>().CreateAsync(
                profileId,
                new TitleKey(MediaKind.Movie, 700),
                "Movie 700",
                RequestActor.Kid(profileId, "Ada"));
            Assert.Equal(RequestOutcome.Created, result.Outcome);
        }

        factory.Tmdb.Enqueue("/3/movie/700/release_dates", StatusCodes.Status500InternalServerError);
        JsonElement item = Assert.Single((await QueueAsync(await factory.CreateAuthorizedClientAsync())).GetProperty("items").EnumerateArray());
        Assert.Equal("A short story.", item.GetProperty("overview").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("rating").ValueKind);
        Assert.Equal(["Stream"], item.GetProperty("providers").EnumerateArray().Select(provider => provider.GetProperty("name").GetString()));
        Assert.Equal(["rating"], Texts(item.GetProperty("gaps")));
        Assert.Equal("unavailable", item.GetProperty("reason").GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_approved_request_leaves_the_queue()
    {
        using KidRequestApiFactory factory = new();
        await factory.LinkPlexAsync();
        (HttpClient kid, _) = await factory.SignInKidAsync();
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        int id = await AskAsync(kid, 700);
        HttpResponseMessage decision = await parent.PostAsJsonAsync("/api/v1/requests/approve", new { ids = new[] { id } });
        Assert.Equal(HttpStatusCode.OK, decision.StatusCode);

        JsonElement body = await QueueAsync(parent);
        Assert.Empty(body.GetProperty("items").EnumerateArray());
        Assert.Equal(0, body.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task The_queue_filters_by_kid_and_rejects_bad_paging()
    {
        using KidRequestApiFactory factory = new();
        (HttpClient ada, int adaId) = await factory.SignInKidAsync("Ada");
        (HttpClient bea, int beaId) = await factory.SignInKidAsync("Bea");
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        int first = await AskAsync(ada, 700);
        int second = await AskAsync(bea, 701);

        JsonElement all = await QueueAsync(parent);
        JsonElement adaOnly = await QueueAsync(parent, $"kidProfileId={adaId}");
        JsonElement page = await QueueAsync(parent, "limit=1");
        Assert.Equal([first, second], Ids(all));
        Assert.Equal([first], Ids(adaOnly));
        Assert.Equal(adaId, adaOnly.GetProperty("items")[0].GetProperty("kidProfileId").GetInt32());
        Assert.Equal(1, page.GetProperty("items").GetArrayLength());
        Assert.Equal(2, page.GetProperty("total").GetInt32());
        Assert.Equal(1, page.GetProperty("limit").GetInt32());

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
            KidProfile profile = await db.KidProfiles.SingleAsync(row => row.Id == adaId);
            profile.ArchivedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        JsonElement archived = await QueueAsync(parent, $"kidProfileId={adaId}");
        Assert.Equal([first], Ids(archived));
        Assert.Equal(2, (await QueueAsync(parent)).GetProperty("total").GetInt32());

        await AssertProblemAsync(parent, "kidProfileId=0", HttpStatusCode.BadRequest, "The kid profile id must be a positive number.");
        await AssertProblemAsync(parent, "kidProfileId=-3", HttpStatusCode.BadRequest, "The kid profile id must be a positive number.");
        await AssertProblemAsync(parent, $"kidProfileId={beaId + 1000}", HttpStatusCode.NotFound, "The kid profile does not exist.");
        await AssertProblemAsync(parent, "limit=0", HttpStatusCode.BadRequest, "The limit is 1 to 200 and the offset is not negative.");
        await AssertProblemAsync(parent, "offset=-1", HttpStatusCode.BadRequest, "The limit is 1 to 200 and the offset is not negative.");
    }

    [Fact]
    public async Task A_changed_block_list_names_the_genre_keyword_or_language()
    {
        using KidRequestApiFactory factory = new();
        factory.Tmdb.SetBody("/3/movie/700", """
            {"id":700,"title":"Movie 700","overview":"Ada's film.","genres":[{"id":28,"name":"Action"}],"original_language":"en","vote_average":7}
            """);
        factory.Tmdb.SetBody("/3/movie/701", """
            {"id":701,"title":"Movie 701","overview":"Bea's film.","genres":[{"id":35,"name":"Comedy"}],"original_language":"en","vote_average":7}
            """);
        factory.Tmdb.SetBody("/3/movie/701/keywords", """{"id":701,"keywords":[{"id":1721,"name":"fight"}]}""");
        factory.Tmdb.SetBody("/3/movie/702", """
            {"id":702,"title":"Movie 702","overview":"Cy's film.","genres":[{"id":35,"name":"Comedy"}],"original_language":"en","vote_average":7}
            """);
        factory.Tmdb.SetBody("/3/movie/704", """
            {"id":704,"title":"Movie 704","overview":"Dee's film.","genres":[{"id":35,"name":"Comedy"}],"original_language":"en","vote_average":7}
            """);
        factory.Tmdb.SetBody("/3/movie/704/release_dates", """
            {"results":[{"iso_3166_1":"US","release_dates":[{"certification":"PG","type":3}]}]}
            """);
        foreach (int id in new[] { 700, 701, 702, 704 })
        {
            factory.Tmdb.SetBody($"/3/movie/{id}/watch/providers", """{"results":{"US":{"flatrate":[{"provider_id":8,"provider_name":"Stream"}]}}}""");
        }

        (HttpClient ada, int adaId) = await factory.SignInKidAsync("Ada");
        (HttpClient bea, int beaId) = await factory.SignInKidAsync("Bea");
        (HttpClient cy, int cyId) = await factory.SignInKidAsync("Cy");
        (HttpClient dee, int deeId) = await factory.SignInKidAsync("Dee");
        await AskAsync(ada, 700);
        await AskAsync(bea, 701);
        await AskAsync(cy, 702);
        await AskAsync(dee, 704);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
            (await db.KidProfiles.SingleAsync(row => row.Id == adaId)).BlockedGenreIds = [28];
            (await db.KidProfiles.SingleAsync(row => row.Id == beaId)).BlockedKeywordIds = [1721];
            (await db.KidProfiles.SingleAsync(row => row.Id == cyId)).AllowedOriginalLanguages = ["ja"];
            (await db.KidProfiles.SingleAsync(row => row.Id == deeId)).BlockedKeywordIds = [99];
            await db.SaveChangesAsync();
        }

        factory.Tmdb.Enqueue("/3/movie/704/keywords", StatusCodes.Status500InternalServerError);
        JsonElement body = await QueueAsync(await factory.CreateAuthorizedClientAsync());
        JsonElement adaItem = ItemFor(body, adaId);
        JsonElement beaItem = ItemFor(body, beaId);
        JsonElement cyItem = ItemFor(body, cyId);
        JsonElement deeItem = ItemFor(body, deeId);

        Assert.Equal("blockedGenre", adaItem.GetProperty("reason").GetProperty("code").GetString());
        Assert.Equal("Action", adaItem.GetProperty("reason").GetProperty("genreName").GetString());
        Assert.Equal("Ada's film.", adaItem.GetProperty("overview").GetString());
        Assert.Equal("blockedKeyword", beaItem.GetProperty("reason").GetProperty("code").GetString());
        Assert.Equal("fight", beaItem.GetProperty("reason").GetProperty("keywordName").GetString());
        Assert.Equal("languageNotAllowed", cyItem.GetProperty("reason").GetProperty("code").GetString());
        Assert.Equal("en", cyItem.GetProperty("reason").GetProperty("language").GetString());
        Assert.Equal("unavailable", deeItem.GetProperty("reason").GetProperty("code").GetString());
        Assert.Equal("Dee's film.", deeItem.GetProperty("overview").GetString());
        Assert.Equal("PG", deeItem.GetProperty("rating").GetString());
        Assert.Equal(["Stream"], deeItem.GetProperty("providers").EnumerateArray().Select(provider => provider.GetProperty("name").GetString()));
        Assert.Equal(["keywords"], Texts(deeItem.GetProperty("gaps")));
        Assert.Empty(adaItem.GetProperty("gaps").EnumerateArray());
    }

    [Fact]
    public async Task A_title_blocked_after_the_kid_asked_is_explicitly_blocked_like_the_preview()
    {
        using KidRequestApiFactory factory = new();
        (HttpClient kid, int profileId) = await factory.SignInKidAsync();
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        await AskAsync(kid, 700);
        await AddTitleRuleAsync(parent, profileId, 700, blocked: true);

        JsonElement reason = Assert.Single((await QueueAsync(parent)).GetProperty("items").EnumerateArray()).GetProperty("reason");
        JsonElement preview = await PreviewAsync(parent, profileId, 700);

        Assert.Equal("blocked", preview.GetProperty("decision").GetString());
        Assert.Equal("explicitlyBlocked", preview.GetProperty("reasonCode").GetString());
        Assert.Equal("explicitlyBlocked", reason.GetProperty("code").GetString());
        Assert.Equal("PG", reason.GetProperty("rating").GetString());
    }

    [Fact]
    public async Task An_always_allowed_title_above_the_age_limit_is_not_called_above_the_limit()
    {
        using KidRequestApiFactory factory = new();
        (HttpClient kid, int profileId) = await factory.SignInKidAsync(movieAgeLimit: 8);
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await KidRequestApiFactory.RequestAsync(kid, "movie", 603)).StatusCode);
        await AddTitleRuleAsync(parent, profileId, 603, blocked: false);
        await AskAsync(kid, 603);

        JsonElement item = Assert.Single((await QueueAsync(parent)).GetProperty("items").EnumerateArray());
        JsonElement reason = item.GetProperty("reason");
        JsonElement preview = await PreviewAsync(parent, profileId, 603);

        Assert.Equal("allowed", preview.GetProperty("decision").GetString());
        Assert.Equal("explicitlyAllowed", preview.GetProperty("reasonCode").GetString());
        Assert.Equal("autoApproveOff", reason.GetProperty("code").GetString());
        Assert.Equal("PG-13", reason.GetProperty("rating").GetString());
        Assert.Equal("teen", reason.GetProperty("level").GetString());
        Assert.Equal(JsonValueKind.Null, reason.GetProperty("maxLevel").ValueKind);
        Assert.Equal("A hacker learns the truth.", item.GetProperty("overview").GetString());
    }

    [Fact]
    public async Task An_adult_title_is_unavailable_like_the_preview_and_shows_nothing_from_tmdb()
    {
        using KidRequestApiFactory factory = new();
        factory.Tmdb.SetBody("/3/movie/705", """
            {"id":705,"title":"Movie 705","overview":"Not for kids.","adult":true,"genres":[{"id":18,"name":"Drama"}],"original_language":"en","vote_average":6}
            """);
        factory.Tmdb.SetBody("/3/movie/705/release_dates", """{"results":[{"iso_3166_1":"US","release_dates":[{"certification":"PG","type":3}]}]}""");
        factory.Tmdb.SetBody("/3/movie/705/watch/providers", """{"results":{"US":{"flatrate":[{"provider_id":8,"provider_name":"Stream"}]}}}""");
        (_, int profileId) = await factory.SignInKidAsync();
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        await CreatePendingAsync(factory, profileId, 705, "Kept Title");

        JsonElement item = Assert.Single((await QueueAsync(parent)).GetProperty("items").EnumerateArray());
        JsonElement preview = await PreviewAsync(parent, profileId, 705);

        Assert.Equal("blocked", preview.GetProperty("decision").GetString());
        Assert.Equal("metadataUnavailable", preview.GetProperty("reasonCode").GetString());
        Assert.Equal("unavailable", item.GetProperty("reason").GetProperty("code").GetString());
        Assert.Equal("Kept Title", item.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("overview").ValueKind);
        Assert.Empty(item.GetProperty("providers").EnumerateArray());
        Assert.Empty(item.GetProperty("gaps").EnumerateArray());
        Assert.Equal(0, factory.Tmdb.Count("/3/movie/705/watch/providers"));
    }

    [Fact]
    public async Task An_explicit_block_wins_over_an_earlier_revocation_like_the_preview()
    {
        using KidRequestApiFactory factory = new();
        (HttpClient kid, int profileId) = await factory.SignInKidAsync();
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        int revoked = await AskAsync(kid, 700);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
            MediaRequest row = await db.MediaRequests.SingleAsync(request => request.Id == revoked);
            row.State = RequestState.Revoked;
            await db.SaveChangesAsync();
        }

        int waiting = await AskAsync(kid, 700);
        Assert.Equal("revokedBefore", ItemFor(await QueueAsync(parent), profileId).GetProperty("reason").GetProperty("code").GetString());
        await AddTitleRuleAsync(parent, profileId, 700, blocked: true);

        JsonElement item = Assert.Single((await QueueAsync(parent)).GetProperty("items").EnumerateArray());
        JsonElement preview = await PreviewAsync(parent, profileId, 700);

        Assert.Equal(waiting, item.GetProperty("id").GetInt32());
        Assert.Equal("blocked", preview.GetProperty("decision").GetString());
        Assert.Equal("explicitlyBlocked", preview.GetProperty("reasonCode").GetString());
        Assert.Equal("explicitlyBlocked", item.GetProperty("reason").GetProperty("code").GetString());
    }

    private static async Task AddTitleRuleAsync(HttpClient parent, int profileId, int tmdbId, bool blocked)
    {
        HttpResponseMessage response = await parent.PostAsJsonAsync(
            $"/api/v1/kid-profiles/{profileId}/titles",
            new { kind = "movie", tmdbId, title = $"Movie {tmdbId}", blocked });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static Task<JsonElement> PreviewAsync(HttpClient parent, int profileId, int tmdbId) =>
        parent.PostAsJsonAsync($"/api/v1/kid-profiles/{profileId}/preview", new { kind = "movie", tmdbId }).ResultBody();

    private static async Task CreatePendingAsync(KidRequestApiFactory factory, int profileId, int tmdbId, string title)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        RequestResult result = await scope.ServiceProvider.GetRequiredService<MediaRequests>().CreateAsync(
            profileId,
            new TitleKey(MediaKind.Movie, tmdbId),
            title,
            RequestActor.Kid(profileId, "Ada"));
        Assert.Equal(RequestOutcome.Created, result.Outcome);
    }

    private static async Task<int> AskAsync(HttpClient kid, int tmdbId)
    {
        HttpResponseMessage response = await KidRequestApiFactory.RequestAsync(kid, "movie", tmdbId);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private static async Task<JsonElement> QueueAsync(HttpClient parent, string? query = null)
    {
        HttpResponseMessage response = await parent.GetAsync(query is null ? QueuePath : $"{QueuePath}?{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();
    }

    private static async Task AssertProblemAsync(HttpClient parent, string query, HttpStatusCode status, string title)
    {
        HttpResponseMessage response = await parent.GetAsync($"{QueuePath}?{query}");
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(title, body.GetProperty("title").GetString());
    }

    private static JsonElement ItemFor(JsonElement body, int profileId) =>
        body.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("kidProfileId").GetInt32() == profileId);

    private static int[] Ids(JsonElement body) =>
        [.. body.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt32())];

    private static string[] Texts(JsonElement array) =>
        [.. array.EnumerateArray().Select(item => item.GetString()!)];

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out IEnumerable<string>? values) ? string.Join(',', values) : string.Empty;
}
