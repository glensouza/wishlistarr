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
        using GuardianApiFactory factory = new();
        await factory.LinkAsync();
        int ada = await factory.CreateProfileAsync("Ada");
        await factory.LabelAsync("101", "Ada", ada);
        await factory.RunJobAsync(LibraryFullRefreshJob.JobName);
        factory.Fake.ReAddItem("101", "1101", newGuid);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
            db.LibraryItems.Add(new LibraryItem
            {
                ServerId = FakePlexServer.MachineIdentifier,
                RatingKey = "1101",
                Guid = newGuid,
                Kind = MediaKind.Movie,
                LibraryId = "1",
                Title = "The Matrix",
                ExternalIds = [new LibraryExternalId { Source = LibraryIdSource.Tmdb, Value = "603" }],
            });
            await db.SaveChangesAsync();
        }

        HttpClient client = await factory.CreateAuthorizedClientAsync();

        await factory.RunJobAsync(LabelGuardianJob.JobName);
        JsonElement report = await factory.LatestReportAsync(client);

        JsonElement[] entries = [.. report.GetProperty("entries").EnumerateArray()];
        JsonElement moved = Assert.Single(entries, entry => entry.GetProperty("kind").GetString() == "moved");
        Assert.Equal(MatrixGuid, moved.GetProperty("previousGuid").GetString());
        Assert.Equal(newGuid, moved.GetProperty("guid").GetString());
        Assert.Equal("repaired", Assert.Single(entries, entry => entry.GetProperty("kind").GetString() == "missing").GetProperty("action").GetString());
        Assert.Equal(["Ada"], factory.Fake.Item("1101").Labels);
        OwnedLabel row = Assert.Single(await factory.GetLedgerAsync());
        Assert.Equal(newGuid, row.Guid);
        Assert.Equal("1101", row.RatingKey);
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
