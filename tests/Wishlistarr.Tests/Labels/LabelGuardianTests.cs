using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wishlistarr.Api.Jobs;
using Wishlistarr.Api.Labels;
using Wishlistarr.Api.Library;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Plex;
using Wishlistarr.Tests.Plex;

namespace Wishlistarr.Tests.Labels;

public sealed class GuardianApiFactory : AuthApiFactory
{
    public GuardianApiFactory() => this.Fake = FakePlexServer.StartAsync().GetAwaiter().GetResult();

    public FakePlexServer Fake { get; }

    public TestClock Clock { get; } = new();

    public int Puts => this.Fake.Requests.Count(request => request.Method == "PUT");

    public async Task LinkAsync()
    {
        IMediaServerAccount account = this.Services.GetRequiredService<IMediaServerAccount>();
        MediaServerLinkStart link = await account.StartLinkAsync();
        this.Fake.PinApproved = true;
        await account.CheckLinkAsync(link.Id);
        Assert.NotNull(await account.SelectServerAsync(FakePlexServer.MachineIdentifier));
    }

    public async Task<int> CreateProfileAsync(string name, bool archived = false)
    {
        using IServiceScope scope = this.Services.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        KidProfile profile = new()
        {
            Name = name,
            Avatar = "fox",
            PlexLabel = name,
            CreatedAt = this.Clock.GetUtcNow(),
            ArchivedAt = archived ? this.Clock.GetUtcNow() : null,
        };
        db.KidProfiles.Add(profile);
        await db.SaveChangesAsync();
        return profile.Id;
    }

    public async Task LabelAsync(string itemId, string label, int profileId)
    {
        using IServiceScope scope = this.Services.CreateScope();
        LabelWriteResult result = await scope.ServiceProvider.GetRequiredService<LabelWriter>().AddAsync(itemId, label, profileId, "request");
        Assert.Equal(LabelWriteOutcome.Added, result.Outcome);
    }

    public async Task<List<OwnedLabel>> GetLedgerAsync()
    {
        using IServiceScope scope = this.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().OwnedLabels
            .AsNoTracking()
            .OrderBy(label => label.Id)
            .ToListAsync();
    }

    public async Task IndexAsync(string ratingKey, string guid, string title, params (LibraryIdSource Source, string Value)[] ids)
    {
        using IServiceScope scope = this.Services.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        db.LibraryItems.Add(new LibraryItem
        {
            ServerId = FakePlexServer.MachineIdentifier,
            RatingKey = ratingKey,
            Guid = guid,
            Kind = MediaKind.Movie,
            LibraryId = "1",
            Title = title,
            ExternalIds = [.. ids.Select(id => new LibraryExternalId { Source = id.Source, Value = id.Value })],
        });
        await db.SaveChangesAsync();
    }

    public async Task AddLedgerRowAsync(string guid, string ratingKey, string label, int profileId)
    {
        using IServiceScope scope = this.Services.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        db.OwnedLabels.Add(new OwnedLabel
        {
            ServerId = FakePlexServer.MachineIdentifier,
            Guid = guid,
            RatingKey = ratingKey,
            Label = label,
            KidProfileId = profileId,
            Source = "request",
        });
        await db.SaveChangesAsync();
    }

    public async Task ArchiveAsync(int profileId)
    {
        using IServiceScope scope = this.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().KidProfiles
            .Where(profile => profile.Id == profileId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(profile => profile.ArchivedAt, this.Clock.GetUtcNow()));
    }

    public async Task<List<LabelGuardianReport>> GetReportsAsync()
    {
        using IServiceScope scope = this.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().LabelGuardianReports
            .AsNoTracking()
            .Include(report => report.Entries.OrderBy(entry => entry.Id))
            .OrderBy(report => report.Id)
            .ToListAsync();
    }

    public async Task<int> CountReportsAsync()
    {
        using IServiceScope scope = this.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().LabelGuardianReports.CountAsync();
    }

    public async Task<JobRun> RunJobAsync(string name)
    {
        JobRunner runner = this.Services.GetRequiredService<JobRunner>();
        JobStartResult started = await runner.StartAsync(name, JobRunTrigger.Manual);
        Assert.Equal(JobStartStatus.Started, started.Status);
        await started.Completion;
        return (await runner.ListRunsAsync(name, 1)).Single();
    }

    public async Task<JsonElement> LatestReportAsync(HttpClient client)
    {
        JsonElement page = await client.GetFromJsonAsync<JsonElement>("/api/v1/labels/guardian/reports");
        int id = page.GetProperty("items")[0].GetProperty("id").GetInt32();
        return await client.GetFromJsonAsync<JsonElement>($"/api/v1/labels/guardian/reports/{id}");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        foreach (string job in new[] { LibraryFullRefreshJob.JobName, LibraryIncrementalRefreshJob.JobName, LabelGuardianJob.JobName })
        {
            builder.UseSetting($"Jobs:Schedules:{job}:Enabled", "false");
        }

        builder.ConfigureTestServices(services =>
        {
            services.Configure<PlexOptions>(options =>
            {
                options.PlexTvBaseAddress = this.Fake.Address;
                options.ProbeTimeout = TimeSpan.FromSeconds(3);
            });
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(this.Clock);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            this.Fake.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}

public sealed class LabelGuardianTests
{
    private const string MatrixGuid = "plex://movie/5d7768ba96b655001fdc0408";

    [Fact]
    public async Task Missing_and_extra_labels_are_reported_per_profile_and_only_owned_missing_labels_are_put_back()
    {
        using GuardianApiFactory factory = new();
        await factory.LinkAsync();
        int ada = await factory.CreateProfileAsync("Ada");
        int ben = await factory.CreateProfileAsync("Ben");
        await factory.LabelAsync("101", "Ada", ada);
        await factory.LabelAsync("102", "Ben", ben);
        await factory.RunJobAsync(LibraryFullRefreshJob.JobName);
        factory.Fake.Item("101").SetLabels("Favorites");
        factory.Fake.Item("201").SetLabels("Ada");
        factory.Fake.Item("102").SetLabels("Ben", "Favorites");
        HttpClient client = await factory.CreateAuthorizedClientAsync();

        JobRun run = await factory.RunJobAsync(LabelGuardianJob.JobName);
        JsonElement report = await factory.LatestReportAsync(client);

        Assert.Equal(JobRunOutcome.Succeeded, run.Outcome);
        JsonElement summary = report.GetProperty("report");
        Assert.False(summary.GetProperty("dryRun").GetBoolean());
        Assert.Equal(2, summary.GetProperty("ledgerChecked").GetInt32());
        Assert.True(summary.GetProperty("extrasChecked").GetBoolean());
        Assert.Equal(1, summary.GetProperty("missing").GetInt32());
        Assert.Equal(1, summary.GetProperty("extra").GetInt32());
        Assert.Equal(1, summary.GetProperty("repaired").GetInt32());
        JsonElement profile = Assert.Single(report.GetProperty("profiles").EnumerateArray());
        Assert.Equal(ada, profile.GetProperty("kidProfileId").GetInt32());
        Assert.Equal("Ada", profile.GetProperty("profileName").GetString());
        Assert.Equal(1, profile.GetProperty("missing").GetInt32());
        Assert.Equal(1, profile.GetProperty("extra").GetInt32());
        JsonElement[] entries = [.. report.GetProperty("entries").EnumerateArray()];
        JsonElement missing = Assert.Single(entries, entry => entry.GetProperty("kind").GetString() == "missing");
        Assert.Equal("repaired", missing.GetProperty("action").GetString());
        Assert.Equal("101", missing.GetProperty("ratingKey").GetString());
        Assert.Equal("The Matrix", missing.GetProperty("title").GetString());
        JsonElement extra = Assert.Single(entries, entry => entry.GetProperty("kind").GetString() == "extra");
        Assert.Equal("none", extra.GetProperty("action").GetString());
        Assert.Equal("201", extra.GetProperty("ratingKey").GetString());
        Assert.Equal("Ada", extra.GetProperty("label").GetString());

        Assert.Equal(["Favorites", "Ada"], factory.Fake.Item("101").Labels);
        Assert.Equal(["Ada"], factory.Fake.Item("201").Labels);
        Assert.Equal(["Ben", "Favorites"], factory.Fake.Item("102").Labels);
        Assert.Equal(["101", "102"], (await factory.GetLedgerAsync()).Select(row => row.RatingKey));
    }

    [Fact]
    public async Task A_re_added_item_is_found_by_its_guid_and_the_ledger_follows_it_before_the_label_is_put_back()
    {
        using GuardianApiFactory factory = new();
        await factory.LinkAsync();
        int ada = await factory.CreateProfileAsync("Ada");
        await factory.LabelAsync("101", "Ada", ada);
        await factory.RunJobAsync(LibraryFullRefreshJob.JobName);
        factory.Fake.ReAddItem("101", "1101");
        int before = factory.Fake.Requests.Count;
        HttpClient client = await factory.CreateAuthorizedClientAsync();

        await factory.RunJobAsync(LabelGuardianJob.JobName);
        JsonElement report = await factory.LatestReportAsync(client);

        JsonElement[] entries = [.. report.GetProperty("entries").EnumerateArray()];
        JsonElement moved = Assert.Single(entries, entry => entry.GetProperty("kind").GetString() == "moved");
        Assert.Equal("updated", moved.GetProperty("action").GetString());
        Assert.Equal("101", moved.GetProperty("previousRatingKey").GetString());
        Assert.Equal("1101", moved.GetProperty("ratingKey").GetString());
        Assert.Equal(JsonValueKind.Null, moved.GetProperty("previousGuid").ValueKind);
        JsonElement missing = Assert.Single(entries, entry => entry.GetProperty("kind").GetString() == "missing");
        Assert.Equal("repaired", missing.GetProperty("action").GetString());
        Assert.Equal("1101", missing.GetProperty("ratingKey").GetString());

        Assert.Equal(["Ada"], factory.Fake.Item("1101").Labels);
        OwnedLabel row = Assert.Single(await factory.GetLedgerAsync());
        Assert.Equal("1101", row.RatingKey);
        Assert.Equal(MatrixGuid, row.Guid);
        List<FakePlexServer.RecordedRequest> after = [.. factory.Fake.Requests.Skip(before)];
        int search = after.FindIndex(request => request.Query.GetValueOrDefault("guid") == MatrixGuid);
        int put = after.FindIndex(request => request.Method == "PUT");
        Assert.True(search >= 0 && put > search);
        Assert.Equal("1101", after[put].Query["id"]);
        Assert.Single(after, request => request.Method == "PUT");
    }

    [Fact]
    public async Task A_copy_the_library_index_already_knows_is_followed_without_searching_plex()
    {
        using GuardianApiFactory factory = new();
        await factory.LinkAsync();
        int ada = await factory.CreateProfileAsync("Ada");
        await factory.LabelAsync("101", "Ada", ada);
        factory.Fake.ReAddItem("101", "1101");
        await factory.RunJobAsync(LibraryFullRefreshJob.JobName);
        int before = factory.Fake.Requests.Count;

        await factory.RunJobAsync(LabelGuardianJob.JobName);

        Assert.DoesNotContain(factory.Fake.Requests.Skip(before), request => request.Query.ContainsKey("guid"));
        Assert.Equal(["Ada"], factory.Fake.Item("1101").Labels);
        Assert.Equal("1101", Assert.Single(await factory.GetLedgerAsync()).RatingKey);
    }

    [Fact]
    public async Task A_moved_item_that_still_has_its_label_only_moves_the_ledger()
    {
        using GuardianApiFactory factory = new();
        await factory.LinkAsync();
        int ada = await factory.CreateProfileAsync("Ada");
        await factory.LabelAsync("101", "Ada", ada);
        factory.Fake.ReAddItem("101", "1101").SetLabels("Ada");
        int puts = factory.Puts;
        HttpClient client = await factory.CreateAuthorizedClientAsync();

        await factory.RunJobAsync(LabelGuardianJob.JobName);
        JsonElement report = await factory.LatestReportAsync(client);

        JsonElement moved = Assert.Single(report.GetProperty("entries").EnumerateArray());
        Assert.Equal("moved", moved.GetProperty("kind").GetString());
        Assert.Equal("updated", moved.GetProperty("action").GetString());
        Assert.Equal(puts, factory.Puts);
        Assert.Equal("1101", Assert.Single(await factory.GetLedgerAsync()).RatingKey);
    }

    [Fact]
    public async Task An_item_matched_to_a_new_guid_is_found_through_the_external_ids_in_the_library_index()
    {
        const string newGuid = "plex://movie/6a0000000000000000000603";
        const string decoyGuid = "plex://movie/6a0000000000000000000604";
        using GuardianApiFactory factory = new();
        await factory.LinkAsync();
        int ada = await factory.CreateProfileAsync("Ada");
        await factory.LabelAsync("101", "Ada", ada);
        await factory.RunJobAsync(LibraryFullRefreshJob.JobName);
        factory.Fake.ReAddItem("101", "1101", newGuid);
        factory.Fake.AddItem(new FakePlexServer.FakeItem("1102", "movie", decoyGuid, "Decoy", 1) { GuidIds = ["tmdb://604"] });
        await factory.IndexAsync("1101", newGuid, "The Matrix", (LibraryIdSource.Tmdb, "603"));
        await factory.IndexAsync("1102", decoyGuid, "Decoy", (LibraryIdSource.Tmdb, "603"));
        HttpClient client = await factory.CreateAuthorizedClientAsync();

        await factory.RunJobAsync(LabelGuardianJob.JobName);
        JsonElement report = await factory.LatestReportAsync(client);

        JsonElement[] entries = [.. report.GetProperty("entries").EnumerateArray()];
        JsonElement moved = Assert.Single(entries, entry => entry.GetProperty("kind").GetString() == "moved");
        Assert.Equal(MatrixGuid, moved.GetProperty("previousGuid").GetString());
        Assert.Equal(newGuid, moved.GetProperty("guid").GetString());
        Assert.Equal("repaired", Assert.Single(entries, entry => entry.GetProperty("kind").GetString() == "missing").GetProperty("action").GetString());
        Assert.Equal(["Ada"], factory.Fake.Item("1101").Labels);
        Assert.Empty(factory.Fake.Item("1102").Labels);
        OwnedLabel row = Assert.Single(await factory.GetLedgerAsync());
        Assert.Equal(newGuid, row.Guid);
        Assert.Equal("1101", row.RatingKey);
    }

    [Fact]
    public async Task A_fix_match_on_the_same_rating_key_never_moves_the_label_to_the_new_title()
    {
        const string otherGuid = "plex://movie/6b00000000000000000a0001";
        using GuardianApiFactory factory = new();
        await factory.LinkAsync();
        int ada = await factory.CreateProfileAsync("Ada");
        await factory.LabelAsync("101", "Ada", ada);
        await factory.RunJobAsync(LibraryFullRefreshJob.JobName);
        factory.Fake.Rematch("101", otherGuid, "Grown-up Title", "imdb://tt9990001", "tmdb://990001", "tvdb://990002");
        factory.Fake.Item("101").SetLabels();
        List<OwnedLabel> ledger = await factory.GetLedgerAsync();
        int puts = factory.Puts;
        HttpClient client = await factory.CreateAuthorizedClientAsync();

        await factory.RunJobAsync(LabelGuardianJob.JobName);
        JsonElement beforeRefresh = await factory.LatestReportAsync(client);
        await factory.RunJobAsync(LibraryFullRefreshJob.JobName);
        await factory.RunJobAsync(LabelGuardianJob.JobName);
        JsonElement afterRefresh = await factory.LatestReportAsync(client);

        foreach (JsonElement report in new[] { beforeRefresh, afterRefresh })
        {
            JsonElement entry = Assert.Single(report.GetProperty("entries").EnumerateArray());
            Assert.Equal("unresolved", entry.GetProperty("kind").GetString());
            Assert.Equal("none", entry.GetProperty("action").GetString());
            Assert.Equal(MatrixGuid, entry.GetProperty("guid").GetString());
            Assert.Equal(LabelGuardian.NotFoundDetail, entry.GetProperty("detail").GetString());
        }

        Assert.Equal(puts, factory.Puts);
        Assert.Empty(factory.Fake.Item("101").Labels);
        Assert.Equivalent(ledger, await factory.GetLedgerAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Two_items_with_the_same_external_id_are_reported_as_ambiguous_and_left_alone(bool sameGuid)
    {
        const string newGuid = "plex://movie/6a0000000000000000000603";
        string otherGuid = sameGuid ? newGuid : "plex://movie/6a0000000000000000000613";
        using GuardianApiFactory factory = new();
        await factory.LinkAsync();
        int ada = await factory.CreateProfileAsync("Ada");
        await factory.LabelAsync("101", "Ada", ada);
        await factory.RunJobAsync(LibraryFullRefreshJob.JobName);
        factory.Fake.ReAddItem("101", "1101", newGuid);
        factory.Fake.AddItem(new FakePlexServer.FakeItem("1102", "movie", otherGuid, "The Matrix", 1) { GuidIds = ["tmdb://603"] });
        await factory.IndexAsync("1101", newGuid, "The Matrix", (LibraryIdSource.Tmdb, "603"));
        await factory.IndexAsync("1102", otherGuid, "The Matrix", (LibraryIdSource.Tmdb, "603"));
        List<OwnedLabel> ledger = await factory.GetLedgerAsync();
        int puts = factory.Puts;
        HttpClient client = await factory.CreateAuthorizedClientAsync();

        await factory.RunJobAsync(LabelGuardianJob.JobName);
        JsonElement report = await factory.LatestReportAsync(client);

        JsonElement entry = Assert.Single(report.GetProperty("entries").EnumerateArray());
        Assert.Equal("unresolved", entry.GetProperty("kind").GetString());
        Assert.Equal(LabelGuardian.AmbiguousDetail, entry.GetProperty("detail").GetString());
        Assert.Equal(puts, factory.Puts);
        Assert.Empty(factory.Fake.Item("1101").Labels);
        Assert.Empty(factory.Fake.Item("1102").Labels);
        Assert.Equivalent(ledger, await factory.GetLedgerAsync());
    }

    [Fact]
    public async Task A_grant_that_takes_the_new_guid_while_the_guardian_runs_is_recorded_as_a_conflict_and_the_run_goes_on()
    {
        const string newGuid = "plex://movie/6a0000000000000000000603";
        using GuardianApiFactory factory = new();
        await factory.LinkAsync();
        int ada = await factory.CreateProfileAsync("Ada");
        await factory.LabelAsync("101", "Ada", ada);
        await factory.LabelAsync("102", "Ada", ada);
        await factory.RunJobAsync(LibraryFullRefreshJob.JobName);
        factory.Fake.ReAddItem("101", "1101", newGuid);
        factory.Fake.Item("102").SetLabels();
        await factory.IndexAsync("1101", newGuid, "The Matrix", (LibraryIdSource.Tmdb, "603"));
        int granted = 0;
        factory.Fake.Delay = async request =>
        {
            if (request.Path == "/library/metadata/1101" && Interlocked.Exchange(ref granted, 1) == 0)
            {
                await factory.AddLedgerRowAsync(newGuid, "1101", "Ada", ada);
            }
        };
        HttpClient client = await factory.CreateAuthorizedClientAsync();

        JobRun run = await factory.RunJobAsync(LabelGuardianJob.JobName);
        JsonElement report = await factory.LatestReportAsync(client);

        Assert.Equal(JobRunOutcome.Succeeded, run.Outcome);
        JsonElement[] entries = [.. report.GetProperty("entries").EnumerateArray()];
        JsonElement moved = Assert.Single(entries, entry => entry.GetProperty("kind").GetString() == "moved");
        Assert.Equal("failed", moved.GetProperty("action").GetString());
        Assert.Equal(LabelGuardian.ConflictDetail, moved.GetProperty("detail").GetString());
        JsonElement skipped = Assert.Single(entries, entry => entry.GetProperty("kind").GetString() == "missing" && entry.GetProperty("ratingKey").GetString() == "1101");
        Assert.Equal("skipped", skipped.GetProperty("action").GetString());
        JsonElement repaired = Assert.Single(entries, entry => entry.GetProperty("ratingKey").GetString() == "102");
        Assert.Equal("repaired", repaired.GetProperty("action").GetString());
        Assert.Equal(["Ada"], factory.Fake.Item("102").Labels);
        Assert.Equal(["101", "102", "1101"], (await factory.GetLedgerAsync()).Select(row => row.RatingKey));
    }

    [Fact]
    public async Task A_profile_archived_while_the_guardian_runs_does_not_get_its_label_back()
    {
        using GuardianApiFactory factory = new();
        await factory.LinkAsync();
        int ada = await factory.CreateProfileAsync("Ada");
        await factory.LabelAsync("101", "Ada", ada);
        factory.Fake.Item("101").SetLabels();
        int puts = factory.Puts;
        int reads = 0;
        factory.Fake.Delay = async request =>
        {
            if (request.Path == "/library/metadata/101" && Interlocked.Increment(ref reads) == 2)
            {
                await factory.ArchiveAsync(ada);
            }
        };
        HttpClient client = await factory.CreateAuthorizedClientAsync();

        await factory.RunJobAsync(LabelGuardianJob.JobName);
        JsonElement report = await factory.LatestReportAsync(client);

        JsonElement missing = Assert.Single(report.GetProperty("entries").EnumerateArray());
        Assert.Equal("missing", missing.GetProperty("kind").GetString());
        Assert.Equal("skipped", missing.GetProperty("action").GetString());
        Assert.Equal(LabelGuardian.NotOwnedDetail, missing.GetProperty("detail").GetString());
        Assert.Equal(puts, factory.Puts);
        Assert.Empty(factory.Fake.Item("101").Labels);
    }

    [Fact]
    public async Task A_shutdown_after_the_write_reached_plex_is_reported_as_interrupted_not_as_untried()
    {
        using GuardianApiFactory factory = new();
        await factory.LinkAsync();
        int ada = await factory.CreateProfileAsync("Ada");
        await factory.LabelAsync("101", "Ada", ada);
        factory.Fake.Item("101").SetLabels();
        using CancellationTokenSource stopping = new();
        factory.Fake.AfterRequest = request =>
        {
            if (request.Method == "PUT")
            {
                stopping.Cancel();
            }
        };

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            LabelGuardian guardian = scope.ServiceProvider.GetRequiredService<LabelGuardian>();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => guardian.RunAsync(dryRun: false, pruneHistory: true, stopping.Token));
        }

        LabelGuardianEntry entry = Assert.Single(Assert.Single(await factory.GetReportsAsync()).Entries);
        Assert.Equal(LabelDriftKind.Missing, entry.Kind);
        Assert.Equal(LabelDriftAction.Interrupted, entry.Action);
        Assert.Equal(LabelGuardian.InterruptedDetail, entry.Detail);
        Assert.Equal(["Ada"], factory.Fake.Item("101").Labels);
    }

    [Fact]
    public async Task Ownership_of_a_non_ascii_label_follows_the_ledger_collation()
    {
        const string upperName = "\u00C9lodie";
        const string lowerName = "\u00E9lodie";
        using GuardianApiFactory factory = new();
        await factory.LinkAsync();
        int upper = await factory.CreateProfileAsync(upperName);
        int lower = await factory.CreateProfileAsync(lowerName);
        await factory.LabelAsync("101", upperName, upper);
        int puts = factory.Puts;
        HttpClient client = await factory.CreateAuthorizedClientAsync();

        await factory.RunJobAsync(LabelGuardianJob.JobName);
        JsonElement report = await factory.LatestReportAsync(client);

        JsonElement extra = Assert.Single(report.GetProperty("entries").EnumerateArray());
        Assert.Equal("extra", extra.GetProperty("kind").GetString());
        Assert.Equal(lower, extra.GetProperty("kidProfileId").GetInt32());
        Assert.Equal("101", extra.GetProperty("ratingKey").GetString());
        Assert.Equal(puts, factory.Puts);
    }

    [Fact]
    public async Task Extras_are_judged_by_the_guid_plex_reports_now_not_by_a_stale_library_index()
    {
        const string spiritedGuid = "plex://movie/5d776b59ad5437001f79c6f8";
        using GuardianApiFactory factory = new();
        await factory.LinkAsync();
        int ada = await factory.CreateProfileAsync("Ada");
        int old = await factory.CreateProfileAsync("Old", archived: true);
        await factory.RunJobAsync(LibraryFullRefreshJob.JobName);
        await factory.AddLedgerRowAsync(MatrixGuid, "101", "Ada", old);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().LibraryItems
                .Where(item => item.RatingKey == "102")
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Guid, MatrixGuid));
        }

        factory.Fake.Item("102").SetLabels("Ada");
        HttpClient client = await factory.CreateAuthorizedClientAsync();

        await factory.RunJobAsync(LabelGuardianJob.JobName);
        JsonElement report = await factory.LatestReportAsync(client);

        JsonElement extra = Assert.Single(report.GetProperty("entries").EnumerateArray());
        Assert.Equal("extra", extra.GetProperty("kind").GetString());
        Assert.Equal(ada, extra.GetProperty("kidProfileId").GetInt32());
        Assert.Equal("102", extra.GetProperty("ratingKey").GetString());
        Assert.Equal(spiritedGuid, extra.GetProperty("guid").GetString());
    }

    [Fact]
    public async Task An_item_that_cannot_be_found_is_reported_and_left_alone()
    {
        using GuardianApiFactory factory = new();
        await factory.LinkAsync();
        int ada = await factory.CreateProfileAsync("Ada");
        await factory.LabelAsync("101", "Ada", ada);
        await factory.RunJobAsync(LibraryFullRefreshJob.JobName);
        List<OwnedLabel> ledger = await factory.GetLedgerAsync();
        factory.Fake.DeleteItem("101");
        int puts = factory.Puts;
        HttpClient client = await factory.CreateAuthorizedClientAsync();

        JobRun run = await factory.RunJobAsync(LabelGuardianJob.JobName);
        JsonElement report = await factory.LatestReportAsync(client);

        Assert.Equal(JobRunOutcome.Succeeded, run.Outcome);
        JsonElement entry = Assert.Single(report.GetProperty("entries").EnumerateArray());
        Assert.Equal("unresolved", entry.GetProperty("kind").GetString());
        Assert.Equal("none", entry.GetProperty("action").GetString());
        Assert.Equal("101", entry.GetProperty("ratingKey").GetString());
        Assert.Equal(LabelGuardian.NotFoundDetail, entry.GetProperty("detail").GetString());
        Assert.Equal(puts, factory.Puts);
        Assert.Equivalent(ledger, await factory.GetLedgerAsync());
    }

    [Fact]
    public async Task Labels_the_ledger_does_not_own_are_never_added_or_removed()
    {
        using GuardianApiFactory factory = new();
        await factory.LinkAsync();
        int ada = await factory.CreateProfileAsync("Ada");
        int old = await factory.CreateProfileAsync("Old", archived: true);
        await factory.RunJobAsync(LibraryFullRefreshJob.JobName);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
            db.OwnedLabels.Add(new OwnedLabel
            {
                ServerId = FakePlexServer.MachineIdentifier,
                Guid = MatrixGuid,
                RatingKey = "101",
                Label = "Old",
                KidProfileId = old,
                Source = "request",
            });
            await db.SaveChangesAsync();
        }

        factory.Fake.Item("102").SetLabels("Ada");
        int puts = factory.Puts;
        HttpClient client = await factory.CreateAuthorizedClientAsync();

        await factory.RunJobAsync(LabelGuardianJob.JobName);
        JsonElement report = await factory.LatestReportAsync(client);

        Assert.Equal(puts, factory.Puts);
        Assert.Empty(factory.Fake.Item("101").Labels);
        Assert.Equal(["Ada"], factory.Fake.Item("102").Labels);
        Assert.Equal(0, report.GetProperty("report").GetProperty("ledgerChecked").GetInt32());
        JsonElement extra = Assert.Single(report.GetProperty("entries").EnumerateArray());
        Assert.Equal("extra", extra.GetProperty("kind").GetString());
        Assert.Equal(ada, extra.GetProperty("kidProfileId").GetInt32());
        Assert.Equal("Old", Assert.Single(await factory.GetLedgerAsync()).Label);
    }

    [Fact]
    public async Task A_dry_run_reports_drift_without_changing_plex_the_ledger_or_recording_repairs()
    {
        using GuardianApiFactory factory = new();
        await factory.LinkAsync();
        int ada = await factory.CreateProfileAsync("Ada");
        int ben = await factory.CreateProfileAsync("Ben");
        await factory.LabelAsync("101", "Ada", ada);
        await factory.LabelAsync("102", "Ben", ben);
        await factory.RunJobAsync(LibraryFullRefreshJob.JobName);
        factory.Fake.ReAddItem("101", "1101");
        factory.Fake.Item("102").SetLabels();
        List<OwnedLabel> ledger = await factory.GetLedgerAsync();
        int puts = factory.Puts;
        HttpClient client = await factory.CreateAuthorizedClientAsync();

        HttpResponseMessage started = await client.PostAsync($"/api/v1/jobs/{LabelGuardianDryRunJob.JobName}/run", null);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        JsonElement report = await WaitForReportAsync(factory, client);

        Assert.Equal(puts, factory.Puts);
        Assert.Empty(factory.Fake.Item("1101").Labels);
        Assert.Empty(factory.Fake.Item("102").Labels);
        Assert.Equivalent(ledger, await factory.GetLedgerAsync());
        JsonElement summary = report.GetProperty("report");
        Assert.True(summary.GetProperty("dryRun").GetBoolean());
        Assert.Equal(0, summary.GetProperty("repaired").GetInt32());
        Assert.Equal(2, summary.GetProperty("missing").GetInt32());
        Assert.Equal(1, summary.GetProperty("moved").GetInt32());
        string[] actions = [.. report.GetProperty("entries").EnumerateArray().Select(entry => entry.GetProperty("action").GetString()!)];
        Assert.Equal(["wouldUpdate", "wouldRepair", "wouldRepair"], actions);
    }

    [Fact]
    public async Task The_scheduled_job_runs_dry_when_configured_to()
    {
        using GuardianApiFactory factory = new()
        {
            Settings = new Dictionary<string, string?>
            {
                ["Auth:LoginAttemptsPerMinute"] = "1000",
                [$"{LabelGuardianOptions.SectionName}:DryRun"] = "true",
            },
        };
        await factory.LinkAsync();
        int ada = await factory.CreateProfileAsync("Ada");
        await factory.LabelAsync("101", "Ada", ada);
        factory.Fake.Item("101").SetLabels();
        int puts = factory.Puts;
        HttpClient client = await factory.CreateAuthorizedClientAsync();

        await factory.RunJobAsync(LabelGuardianJob.JobName);
        JsonElement report = await factory.LatestReportAsync(client);

        Assert.Equal(puts, factory.Puts);
        Assert.Empty(factory.Fake.Item("101").Labels);
        Assert.True(report.GetProperty("report").GetProperty("dryRun").GetBoolean());
    }

    [Fact]
    public async Task The_scheduled_job_prunes_history_even_when_it_runs_dry_and_a_parent_dry_run_never_does()
    {
        using GuardianApiFactory factory = new()
        {
            Settings = new Dictionary<string, string?>
            {
                ["Auth:LoginAttemptsPerMinute"] = "1000",
                [$"{LabelGuardianOptions.SectionName}:DryRun"] = "true",
                [$"{LabelGuardianOptions.SectionName}:HistoryRetention"] = "30.00:00:00",
            },
        };
        await factory.LinkAsync();
        int ada = await factory.CreateProfileAsync("Ada");
        await factory.LabelAsync("101", "Ada", ada);
        factory.Fake.Item("101").SetLabels();
        List<OwnedLabel> ledger = await factory.GetLedgerAsync();
        int puts = factory.Puts;

        await factory.RunJobAsync(LabelGuardianDryRunJob.JobName);
        int first = Assert.Single(await factory.GetReportsAsync()).Id;
        factory.Clock.Advance(TimeSpan.FromDays(31));
        await factory.RunJobAsync(LabelGuardianDryRunJob.JobName);
        List<int> afterParentRun = [.. (await factory.GetReportsAsync()).Select(report => report.Id)];
        await factory.RunJobAsync(LabelGuardianJob.JobName);
        List<LabelGuardianReport> afterScheduledRun = await factory.GetReportsAsync();

        Assert.Equal(2, afterParentRun.Count);
        Assert.Contains(first, afterParentRun);
        Assert.Equal(2, afterScheduledRun.Count);
        Assert.DoesNotContain(afterScheduledRun, report => report.Id == first);
        Assert.All(afterScheduledRun, report => Assert.True(report.DryRun));
        Assert.Equal(puts, factory.Puts);
        Assert.Empty(factory.Fake.Item("101").Labels);
        Assert.Equivalent(ledger, await factory.GetLedgerAsync());
    }

    [Fact]
    public async Task Repair_history_is_kept_and_listed_newest_first()
    {
        using GuardianApiFactory factory = new();
        await factory.LinkAsync();
        int ada = await factory.CreateProfileAsync("Ada");
        await factory.LabelAsync("101", "Ada", ada);
        factory.Fake.Item("101").SetLabels();
        HttpClient client = await factory.CreateAuthorizedClientAsync();

        await factory.RunJobAsync(LabelGuardianJob.JobName);
        factory.Clock.Advance(TimeSpan.FromDays(1));
        await factory.RunJobAsync(LabelGuardianJob.JobName);
        JsonElement page = await client.GetFromJsonAsync<JsonElement>("/api/v1/labels/guardian/reports");
        JsonElement second = await client.GetFromJsonAsync<JsonElement>("/api/v1/labels/guardian/reports?limit=1&offset=1");

        Assert.Equal(2, page.GetProperty("total").GetInt32());
        JsonElement[] items = [.. page.GetProperty("items").EnumerateArray()];
        Assert.Equal(0, items[0].GetProperty("repaired").GetInt32());
        Assert.Equal(1, items[1].GetProperty("repaired").GetInt32());
        Assert.True(items[0].GetProperty("id").GetInt32() > items[1].GetProperty("id").GetInt32());
        JsonElement first = Assert.Single(second.GetProperty("items").EnumerateArray());
        JsonElement detail = await client.GetFromJsonAsync<JsonElement>($"/api/v1/labels/guardian/reports/{first.GetProperty("id").GetInt32()}");
        JsonElement repaired = Assert.Single(detail.GetProperty("entries").EnumerateArray());
        Assert.Equal("repaired", repaired.GetProperty("action").GetString());
        Assert.Equal(1, second.GetProperty("limit").GetInt32());
        Assert.Equal(1, second.GetProperty("offset").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/labels/guardian/reports?limit=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/labels/guardian/reports?offset=-1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/labels/guardian/reports/999")).StatusCode);
    }

    [Fact]
    public async Task A_plex_failure_during_the_check_records_no_drift_and_repairs_nothing()
    {
        using GuardianApiFactory factory = new();
        await factory.LinkAsync();
        int ada = await factory.CreateProfileAsync("Ada");
        await factory.LabelAsync("101", "Ada", ada);
        await factory.LabelAsync("102", "Ada", ada);
        factory.Fake.Item("101").SetLabels();
        factory.Fake.Item("102").SetLabels();
        List<OwnedLabel> ledger = await factory.GetLedgerAsync();
        int puts = factory.Puts;
        factory.Fake.Delay = request => request.Path == "/library/metadata/102"
            ? throw new InvalidOperationException("The fake server is failing.")
            : Task.CompletedTask;

        JobRun run = await factory.RunJobAsync(LabelGuardianJob.JobName);

        Assert.Equal(JobRunOutcome.Failed, run.Outcome);
        Assert.Equal(0, await factory.CountReportsAsync());
        Assert.Equal(puts, factory.Puts);
        Assert.Empty(factory.Fake.Item("101").Labels);
        Assert.Equivalent(ledger, await factory.GetLedgerAsync());
    }

    [Fact]
    public async Task Without_a_linked_plex_server_the_job_finishes_quietly_without_a_report()
    {
        using GuardianApiFactory factory = new();

        JobRun run = await factory.RunJobAsync(LabelGuardianJob.JobName);

        Assert.Equal(JobRunOutcome.Succeeded, run.Outcome);
        Assert.Equal(0, await factory.CountReportsAsync());
    }

    [Fact]
    public async Task Reports_are_for_parents_only()
    {
        using GuardianApiFactory factory = new();
        await factory.EnsureParentAsync();
        HttpClient anonymous = factory.CreateClient();
        HttpClient kid = factory.CreateClient();
        kid.DefaultRequestHeaders.Add(AuthApiFactory.KidHeader, "1");

        foreach (string path in new[] { "/api/v1/labels/guardian/reports", "/api/v1/labels/guardian/reports/1" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await kid.GetAsync(path)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await kid.PostAsync($"/api/v1/jobs/{LabelGuardianDryRunJob.JobName}/run", null)).StatusCode);
    }

    private static async Task<JsonElement> WaitForReportAsync(GuardianApiFactory factory, HttpClient client)
    {
        JobRunner runner = factory.Services.GetRequiredService<JobRunner>();
        for (int attempt = 0; attempt < 200 && (runner.IsRunning(LabelGuardianDryRunJob.JobName) || await factory.CountReportsAsync() == 0); attempt++)
        {
            await Task.Delay(50);
        }

        return await factory.LatestReportAsync(client);
    }
}
