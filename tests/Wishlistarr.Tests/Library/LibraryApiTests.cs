using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Wishlistarr.Api.Jobs;
using Wishlistarr.Api.Library;
using Wishlistarr.Core.Library;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Tmdb;

namespace Wishlistarr.Tests.Library;

public sealed class LibraryApiFactory : AuthApiFactory
{
    public FakeScanner Scanner { get; } = new();

    public FakeTmdbFinder Tmdb { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting($"Jobs:Schedules:{LibraryFullRefreshJob.JobName}:Enabled", "false");
        builder.UseSetting($"Jobs:Schedules:{LibraryIncrementalRefreshJob.JobName}:Enabled", "false");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ILibraryScanner>();
            services.AddSingleton<ILibraryScanner>(this.Scanner);
            services.RemoveAll<ITmdbClient>();
            services.AddSingleton<ITmdbClient>(this.Tmdb);
        });
    }
}

public sealed class LibraryApiTests(LibraryApiFactory factory) : IClassFixture<LibraryApiFactory>
{
    private static readonly DateTimeOffset Updated = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Both_refresh_jobs_run_through_the_job_runner_and_show_in_system_status()
    {
        factory.Scanner.Entries.Clear();
        factory.Scanner.Entries.Add(FakeScanner.Entry("101", MediaKind.Movie, "The Matrix", [new(ExternalIdSource.Tmdb, "603")], ["Kids-Ada"], Updated));
        factory.Scanner.Entries.Add(FakeScanner.Entry("301", MediaKind.Movie, "Legacy", [new(ExternalIdSource.Imdb, "tt0111161")], updatedAt: Updated));
        factory.Tmdb.Known[(TmdbMediaType.Movie, ExternalIdSource.Imdb, "tt0111161")] = 278;
        HttpClient client = await factory.CreateAuthorizedClientAsync();

        JsonElement before = await client.GetFromJsonAsync<JsonElement>("/api/v1/system/status");
        await RunJobAsync(client, LibraryFullRefreshJob.JobName);
        await RunJobAsync(client, LibraryIncrementalRefreshJob.JobName);
        JsonElement after = await client.GetFromJsonAsync<JsonElement>("/api/v1/system/status");

        Assert.False(before.GetProperty("library").GetProperty("ready").GetBoolean());
        JsonElement library = after.GetProperty("library");
        Assert.True(library.GetProperty("ready").GetBoolean());
        Assert.Equal(2, library.GetProperty("itemCount").GetInt32());
        Assert.Equal(0, library.GetProperty("unresolvedCount").GetInt32());
        Assert.Equal(1, library.GetProperty("labelCount").GetInt32());
        Assert.Equal("server-a", library.GetProperty("serverId").GetString());
        Assert.NotEqual(JsonValueKind.Null, library.GetProperty("lastFullRefreshAt").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, library.GetProperty("lastIncrementalRefreshAt").ValueKind);
        string[] jobs = [.. after.GetProperty("jobs").EnumerateArray().Select(job => job.GetProperty("name").GetString()!)];
        Assert.Contains(LibraryFullRefreshJob.JobName, jobs);
        Assert.Contains(LibraryIncrementalRefreshJob.JobName, jobs);
        foreach (string name in new[] { LibraryFullRefreshJob.JobName, LibraryIncrementalRefreshJob.JobName })
        {
            JsonElement runs = await client.GetFromJsonAsync<JsonElement>($"/api/v1/jobs/{name}/runs");
            Assert.Equal("succeeded", runs.EnumerateArray().First().GetProperty("outcome").GetString());
        }
    }

    [Fact]
    public async Task A_plex_library_webhook_refreshes_the_announced_item()
    {
        factory.Scanner.Entries.Clear();
        factory.Scanner.Entries.Add(FakeScanner.Entry("101", MediaKind.Movie, "The Matrix", [new(ExternalIdSource.Tmdb, "603")], updatedAt: Updated));
        HttpClient client = await factory.CreateAuthorizedClientAsync();
        await RunJobAsync(client, LibraryFullRefreshJob.JobName);
        factory.Scanner.Entries.Add(FakeScanner.Entry("102", MediaKind.Movie, "Spirited Away", [new(ExternalIdSource.Tmdb, "129")], ["Kids-Ada"], Updated));

        HttpResponseMessage response = await this.PostPlexEventAsync(
            client,
            $$$"""{"event":"library.new","Metadata":{"ratingKey":"102","type":"movie","note":"{{{Guid.NewGuid():N}}}"}}""");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        LibraryItem item = await this.WaitForItemAsync("102");
        Assert.Equal("Spirited Away", item.Title);
        Assert.Equal(["Kids-Ada"], item.Labels.Select(label => label.Label));
    }

    [Fact]
    public async Task A_plex_library_webhook_without_an_item_starts_an_incremental_refresh()
    {
        factory.Scanner.Entries.Clear();
        HttpClient client = await factory.CreateAuthorizedClientAsync();
        JobRunner runner = factory.Services.GetRequiredService<JobRunner>();

        HttpResponseMessage response = await this.PostPlexEventAsync(client, $$$"""{"event":"library.new","note":"{{{Guid.NewGuid():N}}}"}""");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        JobRun? run = null;
        for (int attempt = 0; attempt < 100 && run is null; attempt++)
        {
            await Task.Delay(50);
            run = (await runner.ListRunsAsync(LibraryIncrementalRefreshJob.JobName, 20)).FirstOrDefault(item => item.Trigger == JobRunTrigger.Webhook);
        }

        Assert.NotNull(run);
    }

    [Fact]
    public async Task A_plex_library_webhook_does_not_wait_for_a_running_refresh()
    {
        factory.Scanner.Entries.Clear();
        factory.Scanner.Entries.Add(FakeScanner.Entry("101", MediaKind.Movie, "The Matrix", [new(ExternalIdSource.Tmdb, "603")], updatedAt: Updated));
        HttpClient client = await factory.CreateAuthorizedClientAsync();
        await RunJobAsync(client, LibraryFullRefreshJob.JobName);
        factory.Scanner.Entries.Add(FakeScanner.Entry("103", MediaKind.Movie, "Late", [new(ExternalIdSource.Tmdb, "130")], updatedAt: Updated));
        JobRunner runner = factory.Services.GetRequiredService<JobRunner>();
        int earlier = (await runner.ListRunsAsync(LibraryIncrementalRefreshJob.JobName, 100)).Count;
        IDisposable? held = factory.Services.GetRequiredService<LibraryRefreshGate>().TryEnter();
        Assert.NotNull(held);

        try
        {
            HttpResponseMessage response = await this.PostPlexEventAsync(
                client,
                $$$"""{"event":"library.new","Metadata":{"ratingKey":"103","note":"{{{Guid.NewGuid():N}}}"}}""");

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            IReadOnlyList<JobRun> runs = await runner.ListRunsAsync(LibraryIncrementalRefreshJob.JobName, 100);
            for (int attempt = 0; attempt < 100 && runs.Count == earlier; attempt++)
            {
                await Task.Delay(50);
                runs = await runner.ListRunsAsync(LibraryIncrementalRefreshJob.JobName, 100);
            }

            Assert.Equal(earlier + 1, runs.Count);
            Assert.Equal((JobRunTrigger.Webhook, JobRunOutcome.Running), (runs[0].Trigger, runs[0].Outcome));
        }
        finally
        {
            held.Dispose();
        }

        Assert.Equal("Late", (await this.WaitForItemAsync("103")).Title);
    }

    [Fact]
    public async Task Refresh_jobs_succeed_quietly_when_plex_is_not_configured()
    {
        using IServiceScope scope = factory.Services.CreateScope();
        ThrowingScanner scanner = new();
        LibraryRefresh refresh = ActivatorUtilities.CreateInstance<LibraryRefresh>(scope.ServiceProvider, scanner);
        ILogger<LibraryFullRefreshJob> logger = scope.ServiceProvider.GetRequiredService<ILogger<LibraryFullRefreshJob>>();

        await new LibraryFullRefreshJob(refresh, logger).RunAsync(CancellationToken.None);
        await new LibraryIncrementalRefreshJob(refresh, scope.ServiceProvider.GetRequiredService<ILogger<LibraryIncrementalRefreshJob>>()).RunAsync(CancellationToken.None);

        Assert.Equal(2, scanner.Scans);
        Assert.False(await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().LibraryRefreshStates.AnyAsync());
    }

    private static async Task RunJobAsync(HttpClient client, string name)
    {
        HttpResponseMessage started = await client.PostAsync($"/api/v1/jobs/{name}/run", null);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        for (int attempt = 0; attempt < 200; attempt++)
        {
            JsonElement runs = await client.GetFromJsonAsync<JsonElement>($"/api/v1/jobs/{name}/runs");
            if (runs.EnumerateArray().First().GetProperty("outcome").GetString() != "running")
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail("The job did not finish in time.");
    }

    private async Task<HttpResponseMessage> PostPlexEventAsync(HttpClient parent, string body)
    {
        JsonElement hooks = await parent.GetFromJsonAsync<JsonElement>("/api/v1/settings/webhooks");
        string url = hooks.EnumerateArray().Single(hook => hook.GetProperty("source").GetString() == "plex").GetProperty("url").GetString()!;
        return await factory.CreateClient().PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
    }

    private async Task<LibraryItem> WaitForItemAsync(string ratingKey)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            using IServiceScope scope = factory.Services.CreateScope();
            LibraryItem? item = await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().LibraryItems
                .AsNoTracking()
                .Include(row => row.Labels)
                .SingleOrDefaultAsync(row => row.RatingKey == ratingKey);
            if (item is not null)
            {
                return item;
            }

            await Task.Delay(50);
        }

        throw new InvalidOperationException("The item was not indexed in time.");
    }

    private sealed class ThrowingScanner : ILibraryScanner
    {
        public int Scans { get; private set; }

        public Task<string?> GetServerIdAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<LibraryScan> ScanAsync(DateTimeOffset? updatedSince, CancellationToken ct = default)
        {
            this.Scans++;
            throw new MediaServerNotConfiguredException("No Plex server is selected.");
        }

        public Task<LibrarySnapshot?> GetEntryAsync(string itemId, CancellationToken ct = default) =>
            throw new MediaServerNotConfiguredException("No Plex server is selected.");
    }
}
