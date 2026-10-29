using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wishlistarr.Api.Labels;
using Wishlistarr.Core.Content;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Data;
using Wishlistarr.Tests.Plex;
using Wishlistarr.Tests.Requests;

namespace Wishlistarr.Tests.Labels;

public sealed class ObservedProfileLabelGate : IProfileLabelGate
{
    private readonly ProfileLabelGate gate = new();

    public Action? Entering { get; set; }

    public Func<Task>? Entered { get; set; }

    public async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        Task<IDisposable> acquisition = this.gate.EnterAsync(ct);
        this.Entering?.Invoke();
        IDisposable held = await acquisition;
        try
        {
            if (this.Entered is { } entered) await entered().WaitAsync(ct);
            return held;
        }
        catch
        {
            held.Dispose();
            throw;
        }
    }
}

public sealed class BackfillApiFactory : KidRequestApiFactory
{
    public ObservedProfileLabelGate ProfileLabels { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        foreach (string name in new[] { "library-backfill", "library-full-refresh", "library-incremental-refresh", "label-guardian" })
            builder.UseSetting($"Jobs:Schedules:{name}:Enabled", "false");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IProfileLabelGate>();
            services.AddSingleton<IProfileLabelGate>(this.ProfileLabels);
        });
    }

    public async Task<int> PrepareAsync(int? age = 7)
    {
        await this.LinkPlexAsync();
        using IServiceScope scope = this.Services.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        KidProfile profile = new() { Name = "Kid", Avatar = "fox", PlexLabel = "Kid", MovieAgeLimit = age, TvAgeLimit = age };
        db.KidProfiles.Add(profile);
        db.LibraryRefreshStates.Add(new LibraryRefreshState { ServerId = FakePlexServer.MachineIdentifier, LastFullRefreshAt = this.Clock.GetUtcNow() });
        await db.SaveChangesAsync();
        await this.IndexAsync("101", 603);
        await this.IndexAsync("102", 129);
        await this.IndexAsync("201", 82728);
        return profile.Id;
    }

    public async Task IndexAsync(string key, int? tmdbId)
    {
        using IServiceScope scope = this.Services.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        FakePlexServer.FakeItem item = this.Plex.Item(key);
        MediaKind kind = item.Type == "show" ? MediaKind.Show : MediaKind.Movie;
        LibraryTitle? title = tmdbId is null ? null : await db.LibraryTitles.SingleOrDefaultAsync(row => row.Kind == kind && row.TmdbId == tmdbId);
        if (title is null && tmdbId is int id) title = new LibraryTitle { Kind = kind, TmdbId = id };
        db.LibraryItems.Add(new LibraryItem
        {
            ServerId = FakePlexServer.MachineIdentifier,
            RatingKey = key,
            Guid = item.Guid,
            Kind = kind,
            LibraryId = item.Section.ToString(CultureInfo.InvariantCulture),
            Title = item.Title,
            CanonicalTitle = title,
        });
        await db.SaveChangesAsync();
    }

    public async Task<BackfillPreview> PreviewAsync(int profileId, int offset = 0)
    {
        using IServiceScope scope = this.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<LibraryBackfill>().PreviewAsync(profileId, offset, default);
    }

    public async Task<BackfillBatchResponse?> CreateAsync(BackfillPreview preview, bool dryRun = false, params int[] selected)
    {
        using IServiceScope scope = this.Services.CreateScope();
        BackfillSelection[] items = [.. preview.Items.Where(item => selected.Length == 0 ? item.Selectable : selected.Contains(item.LibraryItemId)).Select(item => new BackfillSelection(item.LibraryItemId, item.Guid))];
        return await scope.ServiceProvider.GetRequiredService<LibraryBackfill>().CreateAsync(new BackfillApply(preview.KidProfileId, preview.ServerId, preview.Label, items, dryRun), default);
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        using IServiceScope scope = this.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<LibraryBackfill>().RunAsync(ct);
    }

    public async Task ChangeAsync(int id, bool undo)
    {
        using IServiceScope scope = this.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<LibraryBackfill>().ChangeAsync(id, undo, default);
    }

    public async Task<LibraryBackfillBatch> BatchAsync(int id)
    {
        using IServiceScope scope = this.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().LibraryBackfillBatches.AsNoTracking().Include(batch => batch.Items).SingleAsync(batch => batch.Id == id);
    }
}

public sealed class LibraryBackfillTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mid_batch_profile_changes_pause_without_consuming_pending_items(bool archive)
    {
        using BackfillApiFactory factory = new();
        int profile = await factory.PrepareAsync();
        factory.Plex.Item("102").SetLabels("Kid");
        BackfillBatchResponse batch = (await factory.CreateAsync(await factory.PreviewAsync(profile)))!;
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        TaskCompletionSource confirming = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource mutating = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        factory.Plex.Delay = async request =>
        {
            if (request.Method == "GET" && request.Path == "/library/metadata/101" && Interlocked.Increment(ref reads) == 3)
            {
                confirming.TrySetResult();
                await release.Task;
            }
        };
        Task apply = factory.RunAsync();
        try
        {
            await confirming.Task.WaitAsync(TimeSpan.FromSeconds(10));
            factory.ProfileLabels.Entering = () => mutating.TrySetResult();
            Task<HttpResponseMessage> mutation = archive
                ? parent.DeleteAsync($"/api/v1/kid-profiles/{profile}?removeOwnedLabels=false")
                : parent.PutAsJsonAsync($"/api/v1/kid-profiles/{profile}", new { name = "Kid", avatar = "fox", plexLabel = "Different", movieAgeLimit = 7, tvAgeLimit = 7 });
            await mutating.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(mutation.IsCompleted);
            release.TrySetResult();
            Assert.Equal(archive ? HttpStatusCode.NoContent : HttpStatusCode.OK, (await mutation.WaitAsync(TimeSpan.FromSeconds(10))).StatusCode);
            await apply.WaitAsync(TimeSpan.FromSeconds(10));
            LibraryBackfillBatch paused = await factory.BatchAsync(batch.Id);
            Assert.Equal(BackfillState.Paused, paused.State);
            Assert.Contains("profile was archived or its label changed", paused.Error, StringComparison.Ordinal);
            Assert.Equal(BackfillItemState.Applied, paused.Items.Single(item => item.RatingKey == "101").State);
            Assert.All(paused.Items.Where(item => item.RatingKey != "101"), item =>
            {
                Assert.Equal(BackfillItemState.Pending, item.State);
                Assert.False(item.OwnsLabel);
            });
            Assert.Equal(1, factory.LabelWrites);
            Assert.Single(await factory.GetLedgerAsync());
            Assert.Equal(["Kid"], factory.Plex.Item("102").Labels);
            Assert.Empty(factory.Plex.Item("201").Labels);
            factory.ProfileLabels.Entering = null;
            if (archive)
            {
                await factory.ChangeAsync(batch.Id, true);
                await factory.RunAsync();
                Assert.Equal(BackfillState.Undone, (await factory.BatchAsync(batch.Id)).State);
                Assert.Empty(factory.Plex.Item("101").Labels);
                Assert.Equal(["Kid"], factory.Plex.Item("102").Labels);
                Assert.Empty(await factory.GetLedgerAsync());
            }
            else
            {
                Assert.Equal(HttpStatusCode.OK, (await parent.PutAsJsonAsync($"/api/v1/kid-profiles/{profile}", new { name = "Kid", avatar = "fox", plexLabel = "Kid", movieAgeLimit = 7, tvAgeLimit = 7 })).StatusCode);
                await factory.ChangeAsync(batch.Id, false);
                await factory.RunAsync();
                LibraryBackfillBatch completed = await factory.BatchAsync(batch.Id);
                Assert.Equal(BackfillState.Completed, completed.State);
                Assert.Equal(2, completed.Items.Count(item => item.State == BackfillItemState.Applied));
                Assert.Single(completed.Items, item => item.State == BackfillItemState.Preserved);
                Assert.Equal(2, factory.LabelWrites);
                Assert.Equal(2, (await factory.GetLedgerAsync()).Count);
                Assert.Equal(["Kid"], factory.Plex.Item("201").Labels);
            }
        }
        finally
        {
            release.TrySetResult();
            factory.Plex.Delay = null;
            factory.ProfileLabels.Entering = null;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Profile_label_assignment_waits_for_an_in_flight_undo_confirmation_write_and_verification(bool update)
    {
        using BackfillApiFactory factory = new();
        int profile = await factory.PrepareAsync();
        BackfillPreview preview = await factory.PreviewAsync(profile);
        BackfillBatchResponse batch = (await factory.CreateAsync(preview, false, preview.Items[0].LibraryItemId))!;
        await factory.RunAsync();
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        Assert.Equal(HttpStatusCode.OK, (await parent.PutAsJsonAsync($"/api/v1/kid-profiles/{profile}", new { name = "Kid", avatar = "fox", plexLabel = "Former", movieAgeLimit = 7, tvAgeLimit = 7 })).StatusCode);
        int newer = update ? await CreateOtherProfileAsync(parent) : 0;
        await factory.ChangeAsync(batch.Id, true);
        TaskCompletionSource confirming = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource verifying = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseVerification = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource assigning = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentQueue<string> order = new();
        int reads = 0;
        factory.Plex.Delay = async request =>
        {
            if (request.Method == "GET" && request.Path == "/library/metadata/101")
            {
                int read = Interlocked.Increment(ref reads);
                if (read == 3)
                {
                    confirming.TrySetResult();
                    await release.Task;
                }
                if (read == 4)
                {
                    verifying.TrySetResult();
                    await releaseVerification.Task;
                }
            }
        };
        factory.Plex.AfterRequest = request => { if (request.Method == "PUT") order.Enqueue("removed"); };
        Task undo = factory.RunAsync();
        try
        {
            await confirming.Task.WaitAsync(TimeSpan.FromSeconds(10));
            factory.ProfileLabels.Entering = () => assigning.TrySetResult();
            Task<HttpResponseMessage> assignment = AssignAsync();
            await assigning.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(assignment.IsCompleted);
            using (IServiceScope scope = factory.Services.CreateScope())
                Assert.False(await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().KidProfiles.AnyAsync(row => row.Id != profile && row.ArchivedAt == null && row.PlexLabel == "KID"));
            release.TrySetResult();
            await verifying.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(assignment.IsCompleted);
            Assert.Equal(2, factory.LabelWrites);
            releaseVerification.TrySetResult();
            HttpResponseMessage assigned = await assignment.WaitAsync(TimeSpan.FromSeconds(10));
            await undo.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(update ? HttpStatusCode.OK : HttpStatusCode.Created, assigned.StatusCode);
            Assert.Equal(["removed", "assigned"], order);
            Assert.Equal(BackfillState.UndoPaused, (await factory.BatchAsync(batch.Id)).State);
            Assert.Single(await factory.GetLedgerAsync());
            Assert.Empty(factory.Plex.Item("101").Labels);

            async Task<HttpResponseMessage> AssignAsync()
            {
                HttpResponseMessage response = await AssignProfileLabelAsync(parent, newer);
                order.Enqueue("assigned");
                return response;
            }
        }
        finally
        {
            release.TrySetResult();
            releaseVerification.TrySetResult();
            factory.ProfileLabels.Entering = null;
            factory.Plex.Delay = null;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Undo_waits_for_an_in_flight_profile_assignment_and_preserves_its_access(bool update)
    {
        using BackfillApiFactory factory = new();
        int profile = await factory.PrepareAsync();
        BackfillPreview preview = await factory.PreviewAsync(profile);
        BackfillBatchResponse batch = (await factory.CreateAsync(preview, false, preview.Items[0].LibraryItemId))!;
        await factory.RunAsync();
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        Assert.Equal(HttpStatusCode.OK, (await parent.PutAsJsonAsync($"/api/v1/kid-profiles/{profile}", new { name = "Kid", avatar = "fox", plexLabel = "Former", movieAgeLimit = 7, tvAgeLimit = 7 })).StatusCode);
        int newer = update ? await CreateOtherProfileAsync(parent) : 0;
        TaskCompletionSource assigning = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource removing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.ProfileLabels.Entered = async () =>
        {
            factory.ProfileLabels.Entered = null;
            assigning.TrySetResult();
            await release.Task;
        };
        Task<HttpResponseMessage> assignment = AssignProfileLabelAsync(parent, newer);
        try
        {
            await assigning.Task.WaitAsync(TimeSpan.FromSeconds(10));
            factory.ProfileLabels.Entering = () => removing.TrySetResult();
            await factory.ChangeAsync(batch.Id, true);
            Task undo = factory.RunAsync();
            await removing.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, factory.LabelWrites);
            release.TrySetResult();
            Assert.Equal(update ? HttpStatusCode.OK : HttpStatusCode.Created, (await assignment.WaitAsync(TimeSpan.FromSeconds(10))).StatusCode);
            await undo.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, factory.LabelWrites);
            Assert.Equal(["Kid"], factory.Plex.Item("101").Labels);
            Assert.Equal(BackfillState.UndoPaused, (await factory.BatchAsync(batch.Id)).State);
            Assert.Single(await factory.GetLedgerAsync());
        }
        finally
        {
            release.TrySetResult();
            factory.ProfileLabels.Entered = null;
            factory.ProfileLabels.Entering = null;
        }
    }

    [Fact]
    public async Task Content_import_waits_before_opening_its_write_transaction_so_undo_cleanup_can_finish()
    {
        using BackfillApiFactory factory = new();
        int profile = await factory.PrepareAsync();
        BackfillPreview preview = await factory.PreviewAsync(profile);
        BackfillBatchResponse batch = (await factory.CreateAsync(preview, false, preview.Items[0].LibraryItemId))!;
        await factory.RunAsync();
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        string path = $"/api/v1/kid-profiles/{profile}/content-rules";
        JsonElement rules = await parent.GetFromJsonAsync<JsonElement>(path);
        await factory.ChangeAsync(batch.Id, true);
        TaskCompletionSource cleaning = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource importing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int entries = 0;
        factory.ProfileLabels.Entered = async () =>
        {
            if (Interlocked.Increment(ref entries) == 3)
            {
                cleaning.TrySetResult();
                await release.Task;
            }
        };
        Task undo = factory.RunAsync();
        try
        {
            await cleaning.Task.WaitAsync(TimeSpan.FromSeconds(10));
            factory.ProfileLabels.Entering = () => importing.TrySetResult();
            Task<HttpResponseMessage> import = parent.PutAsJsonAsync(path, rules);
            await importing.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(import.IsCompleted);
            release.TrySetResult();
            Assert.Equal(HttpStatusCode.OK, (await import.WaitAsync(TimeSpan.FromSeconds(10))).StatusCode);
            await undo.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(BackfillState.Undone, (await factory.BatchAsync(batch.Id)).State);
            Assert.Empty(await factory.GetLedgerAsync());
            Assert.Empty(factory.Plex.Item("101").Labels);
        }
        finally
        {
            release.TrySetResult();
            factory.ProfileLabels.Entering = null;
            factory.ProfileLabels.Entered = null;
        }
    }

    private static async Task<int> CreateOtherProfileAsync(HttpClient parent)
    {
        HttpResponseMessage response = await parent.PostAsJsonAsync("/api/v1/kid-profiles", new { name = "New Kid", avatar = "fox", plexLabel = "Other", movieAgeLimit = 7, tvAgeLimit = 7 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private static Task<HttpResponseMessage> AssignProfileLabelAsync(HttpClient parent, int id) => id == 0
        ? parent.PostAsJsonAsync("/api/v1/kid-profiles", new { name = "New Kid", avatar = "fox", plexLabel = "KID", movieAgeLimit = 7, tvAgeLimit = 7 })
        : parent.PutAsJsonAsync($"/api/v1/kid-profiles/{id}", new { name = "New Kid", avatar = "fox", plexLabel = "KID", movieAgeLimit = 7, tvAgeLimit = 7 });

    [Fact]
    public async Task Apply_endpoint_queues_a_background_job_and_exposes_saved_progress_and_results()
    {
        using BackfillApiFactory factory = new();
        int profile = await factory.PrepareAsync();
        BackfillPreview preview = await factory.PreviewAsync(profile);
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        BackfillPreviewItem item = preview.Items.Single(item => item.Outcome == ContentOutcome.Allowed);

        HttpResponseMessage response = await parent.PostAsJsonAsync("/api/v1/labels/backfill/batches", new BackfillApply(profile, preview.ServerId, preview.Label, [new(item.LibraryItemId, item.Guid)]));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        JsonElement accepted = await response.Content.ReadFromJsonAsync<JsonElement>();
        int id = accepted.GetProperty("id").GetInt32();
        Assert.Equal($"/api/v1/labels/backfill/batches/{id}", response.Headers.Location?.ToString());
        for (int attempt = 0; attempt < 100 && (await factory.BatchAsync(id)).State == BackfillState.Applying; attempt++) await Task.Delay(20);
        JsonElement detail = await parent.GetFromJsonAsync<JsonElement>($"/api/v1/labels/backfill/batches/{id}");
        Assert.Equal("completed", detail.GetProperty("batch").GetProperty("state").GetString());
        Assert.Equal(1, detail.GetProperty("batch").GetProperty("applied").GetInt32());
        Assert.Equal("Bluey", Assert.Single(detail.GetProperty("items").EnumerateArray()).GetProperty("title").GetString());
        Assert.Equal(["Kid"], factory.Plex.Item("201").Labels);
        JsonElement list = await parent.GetFromJsonAsync<JsonElement>("/api/v1/labels/backfill/batches");
        Assert.Equal(id, Assert.Single(list.EnumerateArray()).GetProperty("id").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await parent.GetAsync("/api/v1/labels/backfill/batches/999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await parent.PostAsJsonAsync("/api/v1/labels/backfill/batches/999/resume", new { })).StatusCode);
    }

    [Fact]
    public async Task Plex_failure_pauses_and_resume_retries_only_pending_items()
    {
        using BackfillApiFactory factory = new();
        int profile = await factory.PrepareAsync();
        BackfillBatchResponse batch = (await factory.CreateAsync(await factory.PreviewAsync(profile)))!;
        factory.Plex.ServerTokenOverride = "rejected-test-token";
        await factory.RunAsync();
        LibraryBackfillBatch paused = await factory.BatchAsync(batch.Id);
        Assert.Equal(BackfillState.Paused, paused.State);
        Assert.NotNull(paused.Error);
        Assert.All(paused.Items, item => Assert.Equal(BackfillItemState.Pending, item.State));
        Assert.Equal(0, factory.LabelWrites);
        factory.Plex.ServerTokenOverride = null;
        await factory.ChangeAsync(batch.Id, false);
        await factory.RunAsync();
        Assert.Equal(BackfillState.Completed, (await factory.BatchAsync(batch.Id)).State);
        Assert.Equal(3, factory.LabelWrites);
    }

    [Fact]
    public async Task Unlinked_server_pauses_batch_and_relinking_original_server_allows_resume()
    {
        using BackfillApiFactory factory = new();
        int profile = await factory.PrepareAsync();
        BackfillBatchResponse batch = (await factory.CreateAsync(await factory.PreviewAsync(profile)))!;
        await factory.Services.GetRequiredService<IMediaServerAccount>().UnlinkAsync();
        await factory.RunAsync();
        Assert.Equal(BackfillState.Paused, (await factory.BatchAsync(batch.Id)).State);
        Assert.Equal(0, factory.LabelWrites);
        await factory.LinkPlexAsync();
        await factory.ChangeAsync(batch.Id, false);
        await factory.RunAsync();
        Assert.Equal(BackfillState.Completed, (await factory.BatchAsync(batch.Id)).State);
        Assert.Equal(3, factory.LabelWrites);
    }

    [Fact]
    public async Task Preview_lists_allowed_review_blocked_and_unmatched_titles_with_the_shared_policy_reasons_without_writes()
    {
        using BackfillApiFactory factory = new();
        int profile = await factory.PrepareAsync();
        factory.Plex.AddItem(new FakePlexServer.FakeItem("999", "movie", "plex://movie/unmatched", "Unmatched", 1));
        await factory.IndexAsync("999", null);
        HttpClient parent = await factory.CreateAuthorizedClientAsync();

        HttpResponseMessage response = await parent.GetAsync($"/api/v1/labels/backfill/preview?kidProfileId={profile}");
        JsonElement preview = await response.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement[] items = [.. preview.GetProperty("items").EnumerateArray()];

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(4, preview.GetProperty("total").GetInt32());
        Assert.Contains(items, item => item.GetProperty("title").GetString() == "Bluey" && item.GetProperty("outcome").GetString() == "allowed" && item.GetProperty("reason").GetProperty("code").GetString() == "withinRatingLimit");
        Assert.Contains(items, item => item.GetProperty("title").GetString() == "Spirited Away" && item.GetProperty("outcome").GetString() == "needsApproval" && item.GetProperty("reason").GetProperty("code").GetString() == "unrated");
        Assert.Contains(items, item => item.GetProperty("title").GetString() == "The Matrix" && item.GetProperty("outcome").GetString() == "blocked" && item.GetProperty("reason").GetProperty("code").GetString() == "aboveRatingLimit");
        Assert.Contains(items, item => item.GetProperty("title").GetString() == "Unmatched" && !item.GetProperty("selectable").GetBoolean() && item.GetProperty("reason").GetProperty("code").GetString() == "metadataUnavailable");
        Assert.Equal(0, factory.LabelWrites);
        Assert.Empty(await factory.GetLedgerAsync());
        using IServiceScope scope = factory.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().LibraryBackfillBatches.ToListAsync());
    }

    [Fact]
    public async Task Parent_can_exclude_allowed_titles_and_explicitly_include_review_and_blocked_titles_then_undo_only_this_batch()
    {
        using BackfillApiFactory factory = new();
        int profile = await factory.PrepareAsync();
        BackfillPreview preview = await factory.PreviewAsync(profile);
        int[] ids = [.. preview.Items.Where(item => item.Outcome != ContentOutcome.Allowed).Select(item => item.LibraryItemId)];
        BackfillBatchResponse batch = (await factory.CreateAsync(preview, false, ids))!;
        factory.Plex.Item("101").AddLabel("Favorites");

        await factory.RunAsync();

        Assert.Equal(["Favorites", "Kid"], factory.Plex.Item("101").Labels);
        Assert.Equal(["Kid"], factory.Plex.Item("102").Labels);
        Assert.Empty(factory.Plex.Item("201").Labels);
        Assert.Equal(BackfillState.Completed, (await factory.BatchAsync(batch.Id)).State);
        List<OwnedLabel> ledger = await factory.GetLedgerAsync();
        Assert.Equal(2, ledger.Count);
        Assert.All(ledger, row => { Assert.Equal(profile, row.KidProfileId); Assert.Equal(LibraryBackfill.Source(batch.Id), row.Source); });

        await factory.ChangeAsync(batch.Id, undo: true);
        await factory.RunAsync();

        Assert.Equal(["Favorites"], factory.Plex.Item("101").Labels);
        Assert.Empty(factory.Plex.Item("102").Labels);
        Assert.Empty(await factory.GetLedgerAsync());
        Assert.Equal(BackfillState.Undone, (await factory.BatchAsync(batch.Id)).State);
        int writes = factory.LabelWrites;
        await factory.ChangeAsync(batch.Id, undo: true);
        await factory.RunAsync();
        Assert.Equal(writes, factory.LabelWrites);
    }

    [Fact]
    public async Task Dry_run_validates_selection_without_creating_batch_job_ledger_or_plex_changes()
    {
        using BackfillApiFactory factory = new();
        int profile = await factory.PrepareAsync();
        BackfillPreview preview = await factory.PreviewAsync(profile);
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        HttpResponseMessage response = await parent.PostAsJsonAsync("/api/v1/labels/backfill/batches", new BackfillApply(profile, preview.ServerId, preview.Label, [new(preview.Items[0].LibraryItemId, preview.Items[0].Guid)], true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("dryRun").GetBoolean());
        Assert.Equal(1, body.GetProperty("selected").GetInt32());
        Assert.Equal(0, factory.LabelWrites);
        Assert.Empty(await factory.GetLedgerAsync());
        using IServiceScope scope = factory.Services.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        Assert.Equal(0, await db.LibraryBackfillBatches.CountAsync());
        Assert.Equal(0, await db.LibraryBackfillItems.CountAsync());
        Assert.Equal(0, await db.JobRuns.CountAsync(run => run.JobName == LibraryBackfillJob.JobName));
    }

    [Fact]
    public async Task Bounded_chunks_resume_in_new_scopes_and_undo_all_copies_without_claiming_a_preexisting_copy_or_guardian_repair()
    {
        using BackfillApiFactory factory = new();
        int profile = await factory.PrepareAsync();
        string guid = factory.Plex.Item("101").Guid;
        for (int i = 0; i < 28; i++)
        {
            string key = (1000 + i).ToString(CultureInfo.InvariantCulture);
            factory.Plex.AddItem(new FakePlexServer.FakeItem(key, "movie", guid, "Movie copy", 1));
            await factory.IndexAsync(key, 603);
        }
        factory.Plex.Item("1027").AddLabel("Kid");
        factory.Plex.AddItem(new FakePlexServer.FakeItem("2000", "movie", guid, "Excluded copy", 1));
        await factory.IndexAsync("2000", 603);
        BackfillPreview preview = await factory.PreviewAsync(profile);
        int[] ids = [.. preview.Items.Where(item => item.Guid == guid && item.Title != "Excluded copy").Select(item => item.LibraryItemId)];
        BackfillBatchResponse batch = (await factory.CreateAsync(preview, false, ids))!;

        await factory.RunAsync();
        LibraryBackfillBatch part = await factory.BatchAsync(batch.Id);
        Assert.Equal(25, part.Items.Count(item => item.State == BackfillItemState.Applied));
        Assert.Equal(4, part.Items.Count(item => item.State == BackfillItemState.Pending));
        Assert.Equal(BackfillState.Applying, part.State);
        factory.Plex.Item("101").SetLabels();
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<LabelGuardian>().RunAsync(false, false, default);
            LabelWriter writer = scope.ServiceProvider.GetRequiredService<LabelWriter>();
            Assert.Equal(LabelWriteOutcome.NotOwned, (await writer.RestoreAsync("1024", "Kid")).Outcome);
            Assert.Equal(LabelWriteOutcome.NotOwned, (await writer.RestoreAsync("2000", "Kid")).Outcome);
        }
        Assert.Equal(["Kid"], factory.Plex.Item("101").Labels);
        Assert.Empty(factory.Plex.Item("1024").Labels);
        Assert.Empty(factory.Plex.Item("1025").Labels);
        Assert.Empty(factory.Plex.Item("1026").Labels);
        Assert.Empty(factory.Plex.Item("2000").Labels);
        await factory.RunAsync();
        LibraryBackfillBatch complete = await factory.BatchAsync(batch.Id);
        Assert.Equal(28, complete.Items.Count(item => item.State == BackfillItemState.Applied));
        Assert.Single(complete.Items, item => item.State == BackfillItemState.Preserved);
        Assert.Single(await factory.GetLedgerAsync());
        using (IServiceScope scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<LabelGuardian>().RunAsync(false, false, default);
        Assert.Empty(factory.Plex.Item("2000").Labels);

        await factory.ChangeAsync(batch.Id, undo: true);
        factory.Plex.Item("101").SetLabels();
        int writes = factory.LabelWrites;
        using (IServiceScope scope = factory.Services.CreateScope())
            Assert.Equal(LabelWriteOutcome.NotOwned, (await scope.ServiceProvider.GetRequiredService<LabelWriter>().RestoreAsync("101", "Kid")).Outcome);
        Assert.Empty(factory.Plex.Item("101").Labels);
        Assert.Equal(writes, factory.LabelWrites);
        Assert.Equal(LibraryBackfill.Source(batch.Id), Assert.Single(await factory.GetLedgerAsync()).Source);
        await factory.RunAsync();
        Assert.Equal(BackfillState.Undoing, (await factory.BatchAsync(batch.Id)).State);
        Assert.Equal(LibraryBackfill.UndoSource(batch.Id), Assert.Single(await factory.GetLedgerAsync()).Source);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<LabelGuardian>().RunAsync(false, false, default);
            Assert.Equal(LabelWriteOutcome.NotOwned, (await scope.ServiceProvider.GetRequiredService<LabelWriter>().RestoreAsync("101", "Kid")).Outcome);
        }
        await factory.RunAsync();
        Assert.Equal(["Kid"], factory.Plex.Item("1027").Labels);
        Assert.Empty(factory.Plex.Item("101").Labels);
        Assert.Empty(factory.Plex.Item("1026").Labels);
        Assert.Empty(factory.Plex.Item("2000").Labels);
        Assert.Empty(await factory.GetLedgerAsync());
        Assert.Equal(BackfillState.Undone, (await factory.BatchAsync(batch.Id)).State);
    }

    [Fact]
    public async Task Cancellation_after_plex_accepts_write_retains_atomic_item_claim_and_ledger_then_resumes_without_a_second_write()
    {
        using BackfillApiFactory factory = new();
        int profile = await factory.PrepareAsync();
        BackfillPreview preview = await factory.PreviewAsync(profile);
        BackfillBatchResponse batch = (await factory.CreateAsync(preview, false, preview.Items[0].LibraryItemId))!;
        using CancellationTokenSource stopped = new();
        factory.Plex.AfterRequest = request => { if (request.Method == "PUT") stopped.Cancel(); };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => factory.RunAsync(stopped.Token));

        Assert.Equal(["Kid"], factory.Plex.Item("101").Labels);
        LibraryBackfillItem item = Assert.Single((await factory.BatchAsync(batch.Id)).Items);
        Assert.True(item.OwnsLabel);
        Assert.Equal(BackfillItemState.Pending, item.State);
        Assert.Equal(LibraryBackfill.Source(batch.Id), Assert.Single(await factory.GetLedgerAsync()).Source);
        factory.Plex.AfterRequest = null;
        await factory.RunAsync();
        Assert.Equal(1, factory.LabelWrites);
        Assert.Equal(BackfillItemState.Applied, Assert.Single((await factory.BatchAsync(batch.Id)).Items).State);
        await factory.ChangeAsync(batch.Id, true);
        await factory.RunAsync();
        Assert.Empty(factory.Plex.Item("101").Labels);
        Assert.Empty(await factory.GetLedgerAsync());
    }

    [Fact]
    public async Task Changed_plex_match_is_skipped_and_existing_external_ownership_is_never_claimed_or_undone()
    {
        using BackfillApiFactory factory = new();
        int profile = await factory.PrepareAsync();
        using (IServiceScope scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<LabelWriter>().AddAsync("102", "Kid", profile, "grant");
        BackfillPreview preview = await factory.PreviewAsync(profile);
        BackfillBatchResponse batch = (await factory.CreateAsync(preview))!;
        factory.Plex.Rematch("101", "plex://movie/changed", "Changed", "tmdb://700");

        await factory.RunAsync();

        Assert.Empty(factory.Plex.Item("101").Labels);
        Assert.Equal(BackfillItemState.Skipped, (await factory.BatchAsync(batch.Id)).Items.Single(item => item.RatingKey == "101").State);
        Assert.Equal("grant", (await factory.GetLedgerAsync()).Single(row => row.RatingKey == "102").Source);
        await factory.ChangeAsync(batch.Id, true);
        await factory.RunAsync();
        Assert.Equal(["Kid"], factory.Plex.Item("102").Labels);
        Assert.Equal("grant", Assert.Single(await factory.GetLedgerAsync()).Source);
    }

    [Fact]
    public async Task Profile_archive_pauses_apply_and_can_be_undone_without_writes()
    {
        using BackfillApiFactory factory = new();
        int profile = await factory.PrepareAsync();
        BackfillPreview preview = await factory.PreviewAsync(profile);
        BackfillBatchResponse batch = (await factory.CreateAsync(preview))!;
        using (IServiceScope scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().KidProfiles.Where(row => row.Id == profile).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ArchivedAt, factory.Clock.GetUtcNow()));
        await factory.RunAsync();
        Assert.Equal(BackfillState.Paused, (await factory.BatchAsync(batch.Id)).State);
        Assert.Equal(0, factory.LabelWrites);
        await factory.ChangeAsync(batch.Id, true);
        await factory.RunAsync();
        Assert.Equal(BackfillState.Undone, (await factory.BatchAsync(batch.Id)).State);
        Assert.Equal(0, factory.LabelWrites);
    }

    [Fact]
    public async Task Undo_preserves_ownership_reassigned_after_apply()
    {
        using BackfillApiFactory factory = new();
        int profile = await factory.PrepareAsync();
        BackfillPreview preview = await factory.PreviewAsync(profile);
        BackfillBatchResponse batch = (await factory.CreateAsync(preview, false, preview.Items[0].LibraryItemId))!;
        await factory.RunAsync();
        using (IServiceScope scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<LabelWriter>().AddAsync("101", "Kid", profile, "grant");

        await factory.ChangeAsync(batch.Id, true);
        await factory.RunAsync();

        Assert.Equal(["Kid"], factory.Plex.Item("101").Labels);
        Assert.Equal("grant", Assert.Single(await factory.GetLedgerAsync()).Source);
        Assert.Equal(1, factory.LabelWrites);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Undo_preserves_reused_profile_labels_and_ledger_before_start_and_between_chunks(bool archive, bool resumed)
    {
        using BackfillApiFactory factory = new();
        int profile = await factory.PrepareAsync();
        string guid = factory.Plex.Item("101").Guid;
        for (int index = 0; index < 26; index++)
        {
            string key = (1000 + index).ToString(CultureInfo.InvariantCulture);
            factory.Plex.AddItem(new FakePlexServer.FakeItem(key, "movie", guid, "Selected copy", 1));
            await factory.IndexAsync(key, 603);
        }
        BackfillPreview preview = await factory.PreviewAsync(profile);
        BackfillBatchResponse batch = (await factory.CreateAsync(preview, false, [.. preview.Items.Where(item => item.Guid == guid).Select(item => item.LibraryItemId)]))!;
        await factory.RunAsync();
        await factory.RunAsync();
        Assert.Equal(27, (await factory.BatchAsync(batch.Id)).Items.Count(item => item.State == BackfillItemState.Applied));
        if (resumed)
        {
            await factory.ChangeAsync(batch.Id, true);
            await factory.RunAsync();
            Assert.Equal(25, (await factory.BatchAsync(batch.Id)).Items.Count(item => item.State == BackfillItemState.Undone));
        }

        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        if (archive)
            Assert.Equal(HttpStatusCode.NoContent, (await parent.DeleteAsync($"/api/v1/kid-profiles/{profile}?removeOwnedLabels=false")).StatusCode);
        else
            Assert.Equal(HttpStatusCode.OK, (await parent.PutAsJsonAsync($"/api/v1/kid-profiles/{profile}", new { name = "Kid", avatar = "fox", plexLabel = "Former", movieAgeLimit = 7, tvAgeLimit = 7 })).StatusCode);
        HttpResponseMessage created = await parent.PostAsJsonAsync("/api/v1/kid-profiles", new { name = "New Kid", avatar = "fox", plexLabel = "KID", movieAgeLimit = 7, tvAgeLimit = 7 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        int newer = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        LibraryBackfillBatch before = await factory.BatchAsync(batch.Id);
        Dictionary<string, string[]> labels = before.Items.ToDictionary(item => item.RatingKey, item => factory.Plex.Item(item.RatingKey).Labels.ToArray());
        OwnedLabel ledger = Assert.Single(await factory.GetLedgerAsync());
        string remaining = before.Items.First(item => item.State == BackfillItemState.Applied).RatingKey;
        using (IServiceScope scope = factory.Services.CreateScope())
            Assert.Equal(LabelWriteOutcome.LabelInUse, (await scope.ServiceProvider.GetRequiredService<LabelWriter>().RemoveBackfillAsync(remaining, "Kid", profile, ledger.Source, preview.ServerId, guid, default)).Outcome);
        await factory.ChangeAsync(batch.Id, !resumed);
        await factory.RunAsync();

        LibraryBackfillBatch paused = await factory.BatchAsync(batch.Id);
        Assert.Equal(BackfillState.UndoPaused, paused.State);
        Assert.Contains("Another active profile uses this label", paused.Error, StringComparison.Ordinal);
        Assert.Equal(before.Items.Count(item => item.State == BackfillItemState.Applied), paused.Items.Count(item => item.State == BackfillItemState.Applied));
        Assert.Equivalent(ledger, Assert.Single(await factory.GetLedgerAsync()));
        foreach ((string key, string[] values) in labels) Assert.Equal(values, factory.Plex.Item(key).Labels);
        await factory.ChangeAsync(batch.Id, false);
        await factory.RunAsync();
        using (IServiceScope scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<LabelGuardian>().RunAsync(false, false, default);
        Assert.Equal(BackfillState.UndoPaused, (await factory.BatchAsync(batch.Id)).State);
        Assert.Equivalent(ledger, Assert.Single(await factory.GetLedgerAsync()));
        foreach ((string key, string[] values) in labels) Assert.Equal(values, factory.Plex.Item(key).Labels);

        Assert.Equal(HttpStatusCode.OK, (await parent.PutAsJsonAsync($"/api/v1/kid-profiles/{newer}", new { name = "New Kid", avatar = "fox", plexLabel = "Different", movieAgeLimit = 7, tvAgeLimit = 7 })).StatusCode);
        await factory.ChangeAsync(batch.Id, false);
        await factory.RunAsync();
        await factory.RunAsync();
        Assert.Equal(BackfillState.Undone, (await factory.BatchAsync(batch.Id)).State);
        Assert.Empty(await factory.GetLedgerAsync());
        foreach (string key in labels.Keys) Assert.Empty(factory.Plex.Item(key).Labels);
    }

    [Fact]
    public async Task Invalid_stale_and_unresolved_selections_are_rejected_without_a_batch()
    {
        using BackfillApiFactory factory = new();
        int profile = await factory.PrepareAsync();
        BackfillPreview preview = await factory.PreviewAsync(profile);
        BackfillSelection one = new(preview.Items[0].LibraryItemId, preview.Items[0].Guid);
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        foreach ((IReadOnlyList<BackfillSelection> items, string server, string label, HttpStatusCode code) in new[]
        {
            ((IReadOnlyList<BackfillSelection>)[], preview.ServerId, preview.Label, HttpStatusCode.BadRequest),
            ([one, one], preview.ServerId, preview.Label, HttpStatusCode.BadRequest),
            ([one with { Guid = "changed" }], preview.ServerId, preview.Label, HttpStatusCode.Conflict),
            ([one], "other-server", preview.Label, HttpStatusCode.Conflict),
            ([one], preview.ServerId, "changed", HttpStatusCode.Conflict),
            ([new BackfillSelection(int.MaxValue, one.Guid)], preview.ServerId, preview.Label, HttpStatusCode.Conflict),
        })
        {
            HttpResponseMessage response = await parent.PostAsJsonAsync("/api/v1/labels/backfill/batches", new BackfillApply(profile, server, label, items));
            Assert.Equal(code, response.StatusCode);
        }
        Assert.Equal(0, factory.LabelWrites);
        Assert.Empty(await factory.GetLedgerAsync());
    }

    [Fact]
    public async Task Preview_requires_ready_index_and_active_profile_and_metadata_failure_disables_selection()
    {
        using BackfillApiFactory factory = new();
        int profile = await factory.PrepareAsync();
        factory.Tmdb.SetBody("/3/movie/603", """{"id":603,"title":"The Matrix","adult":true}""");
        BackfillPreview preview = await factory.PreviewAsync(profile);
        Assert.False(preview.Items.Single(item => item.TmdbId == 603).Selectable);
        HttpClient parent = await factory.CreateAuthorizedClientAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await parent.GetAsync("/api/v1/labels/backfill/preview?kidProfileId=999")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await parent.GetAsync($"/api/v1/labels/backfill/preview?kidProfileId={profile}&offset=-1")).StatusCode);
        using (IServiceScope scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().LibraryRefreshStates.ExecuteDeleteAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await parent.GetAsync($"/api/v1/labels/backfill/preview?kidProfileId={profile}")).StatusCode);
    }

    [Fact]
    public async Task Every_backfill_route_rejects_anonymous_and_kid_sessions()
    {
        using BackfillApiFactory factory = new();
        await factory.EnsureParentAsync();
        HttpClient anonymous = factory.CreateClient();
        HttpClient kid = factory.CreateClient();
        kid.DefaultRequestHeaders.Add(AuthApiFactory.KidHeader, "1");
        foreach (HttpClient client in new[] { anonymous, kid })
        {
            HttpStatusCode expected = client == kid ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized;
            foreach (string path in new[] { "/preview?kidProfileId=1", "/batches", "/batches/1" })
                Assert.Equal(expected, (await client.GetAsync("/api/v1/labels/backfill" + path)).StatusCode);
            foreach (string path in new[] { "/batches", "/batches/1/resume", "/batches/1/undo" })
                Assert.Equal(expected, (await client.PostAsJsonAsync("/api/v1/labels/backfill" + path, new { })).StatusCode);
        }
    }
}
