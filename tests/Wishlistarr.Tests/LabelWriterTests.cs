using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wishlistarr.Api.Labels;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Plex;
using Wishlistarr.Tests.Plex;

namespace Wishlistarr.Tests;

public sealed class LabelApiFactory : ApiFactory
{
    public LabelApiFactory() => this.Fake = FakePlexServer.StartAsync().GetAwaiter().GetResult();

    public FakePlexServer Fake { get; }

    public TestClock Clock { get; } = new();

    public async Task LinkAsync()
    {
        IMediaServerAccount account = this.Services.GetRequiredService<IMediaServerAccount>();
        MediaServerLinkStart link = await account.StartLinkAsync();
        this.Fake.PinApproved = true;
        await account.CheckLinkAsync(link.Id);
        Assert.NotNull(await account.SelectServerAsync(FakePlexServer.MachineIdentifier));
    }

    public async Task<int> CreateProfileAsync(string name)
    {
        using IServiceScope scope = this.Services.CreateScope();
        WishlistarrDbContext db = scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>();
        KidProfile profile = new() { Name = name, Avatar = "fox", PlexLabel = name, CreatedAt = this.Clock.GetUtcNow() };
        db.KidProfiles.Add(profile);
        await db.SaveChangesAsync();
        return profile.Id;
    }

    public async Task<T> WithWriterAsync<T>(Func<LabelWriter, Task<T>> action)
    {
        using IServiceScope scope = this.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<LabelWriter>());
    }

    public async Task<List<OwnedLabel>> GetLedgerAsync()
    {
        using IServiceScope scope = this.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().OwnedLabels
            .AsNoTracking()
            .OrderBy(label => label.Id)
            .ToListAsync();
    }

    public int Puts => this.Fake.Requests.Count(request => request.Method == "PUT");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
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

public sealed class LabelWriterTests
{
    [Fact]
    public async Task Applied_labels_are_recorded_in_the_ledger()
    {
        using LabelApiFactory factory = new();
        await factory.LinkAsync();
        int ada = await factory.CreateProfileAsync("Ada");
        factory.Clock.Advance(TimeSpan.FromMinutes(5));

        LabelWriteResult result = await factory.WithWriterAsync(writer => writer.AddAsync("101", "Ada", ada, "request"));

        Assert.Equal(LabelWriteOutcome.Added, result.Outcome);
        Assert.Equal(["Ada"], factory.Fake.Item("101").Labels);
        OwnedLabel owned = Assert.Single(await factory.GetLedgerAsync());
        Assert.Equal(FakePlexServer.MachineIdentifier, owned.ServerId);
        Assert.Equal("plex://movie/5d7768ba96b655001fdc0408", owned.Guid);
        Assert.Equal("101", owned.RatingKey);
        Assert.Equal("Ada", owned.Label);
        Assert.Equal(ada, owned.KidProfileId);
        Assert.Equal("request", owned.Source);
        Assert.Equal(factory.Clock.GetUtcNow(), owned.AppliedAt);
    }

    [Fact]
    public async Task Restore_puts_back_only_a_label_the_ledger_owns_and_never_creates_a_row()
    {
        using LabelApiFactory factory = new();
        await factory.LinkAsync();
        int ada = await factory.CreateProfileAsync("Ada");
        await factory.WithWriterAsync(writer => writer.AddAsync("101", "Ada", ada, "grant"));
        OwnedLabel before = Assert.Single(await factory.GetLedgerAsync());
        factory.Fake.Item("101").SetLabels("Favorites");
        factory.Clock.Advance(TimeSpan.FromHours(1));

        LabelWriteResult restored = await factory.WithWriterAsync(writer => writer.RestoreAsync("101", "Ada"));
        LabelWriteResult again = await factory.WithWriterAsync(writer => writer.RestoreAsync("101", "Ada"));
        int puts = factory.Puts;
        LabelWriteResult unowned = await factory.WithWriterAsync(writer => writer.RestoreAsync("102", "Ada"));

        Assert.Equal(LabelWriteOutcome.Added, restored.Outcome);
        Assert.Equal(LabelWriteOutcome.AlreadyPresent, again.Outcome);
        Assert.Equal(LabelWriteOutcome.NotOwned, unowned.Outcome);
        Assert.Equal(["Favorites", "Ada"], factory.Fake.Item("101").Labels);
        Assert.Empty(factory.Fake.Item("102").Labels);
        Assert.Equal(puts, factory.Puts);
        OwnedLabel after = Assert.Single(await factory.GetLedgerAsync());
        Assert.Equal(before.Id, after.Id);
        Assert.Equal("grant", after.Source);
        Assert.Equal(before.AppliedAt, after.AppliedAt);
    }

    [Fact]
    public async Task Writes_keep_existing_labels_lock_the_field_and_are_verified()
    {
        using LabelApiFactory factory = new();
        await factory.LinkAsync();
        int ada = await factory.CreateProfileAsync("Ada");
        factory.Fake.Item("101").SetLabels("Favorites", "Ben");

        await factory.WithWriterAsync(writer => writer.AddAsync("101", "Ada", ada, "request"));

        List<FakePlexServer.RecordedRequest> item = [.. factory.Fake.Requests.Where(request => request.Path.StartsWith("/library/", StringComparison.Ordinal))];
        FakePlexServer.RecordedRequest put = Assert.Single(item, request => request.Method == "PUT");
        Assert.Equal("Favorites", put.Query["label[0].tag.tag"]);
        Assert.Equal("Ben", put.Query["label[1].tag.tag"]);
        Assert.Equal("Ada", put.Query["label[2].tag.tag"]);
        Assert.Equal("1", put.Query["label.locked"]);
        int index = item.IndexOf(put);
        Assert.True(index >= 2);
        Assert.All(item[..index], request => Assert.Equal("/library/metadata/101", request.Path));
        Assert.Equal("/library/metadata/101", Assert.Single(item[(index + 1)..]).Path);
        Assert.Equal(["Favorites", "Ben", "Ada"], factory.Fake.Item("101").Labels);
        Assert.True(factory.Fake.Item("101").Locked);
    }

    [Theory]
    [InlineData("201")]
    [InlineData("2011")]
    [InlineData("20111")]
    public async Task Tv_labels_are_applied_to_the_show(string itemId)
    {
        using LabelApiFactory factory = new();
        await factory.LinkAsync();
        int ada = await factory.CreateProfileAsync("Ada");

        LabelWriteResult result = await factory.WithWriterAsync(writer => writer.AddAsync(itemId, "Ada", ada, "request"));

        Assert.Equal(LabelWriteOutcome.Added, result.Outcome);
        FakePlexServer.RecordedRequest put = Assert.Single(factory.Fake.Requests, request => request.Method == "PUT");
        Assert.Equal("/library/sections/2/all", put.Path);
        Assert.Equal("201", put.Query["id"]);
        Assert.Equal("2", put.Query["type"]);
        Assert.Equal(["Ada"], factory.Fake.Item("201").Labels);
        Assert.Empty(factory.Fake.Item("2011").Labels);
        Assert.Empty(factory.Fake.Item("20111").Labels);
        OwnedLabel owned = Assert.Single(await factory.GetLedgerAsync());
        Assert.Equal("201", owned.RatingKey);
        Assert.Equal("plex://show/5d9c086c46115600200aa2fe", owned.Guid);
    }

    [Fact]
    public async Task Labels_the_app_did_not_apply_are_never_removed()
    {
        using LabelApiFactory factory = new();
        await factory.LinkAsync();
        int ada = await factory.CreateProfileAsync("Ada");
        factory.Fake.Item("102").SetLabels("Ben");

        LabelWriteResult foreign = await factory.WithWriterAsync(writer => writer.RemoveAsync("102", "Ben"));
        int putsAfterForeign = factory.Puts;
        await factory.WithWriterAsync(writer => writer.AddAsync("102", "Ada", ada, "request"));
        LabelWriteResult owned = await factory.WithWriterAsync(writer => writer.RemoveAsync("102", "Ada"));

        Assert.Equal(LabelWriteOutcome.NotOwned, foreign.Outcome);
        Assert.Equal(0, putsAfterForeign);
        Assert.Equal(LabelWriteOutcome.Removed, owned.Outcome);
        Assert.Equal(["Ben"], factory.Fake.Item("102").Labels);
        Assert.Empty(await factory.GetLedgerAsync());
    }

    [Fact]
    public async Task Labels_already_on_the_item_are_not_claimed()
    {
        using LabelApiFactory factory = new();
        await factory.LinkAsync();
        int ada = await factory.CreateProfileAsync("Ada");
        factory.Fake.Item("101").SetLabels("ada");

        LabelWriteResult added = await factory.WithWriterAsync(writer => writer.AddAsync("101", "Ada", ada, "request"));
        LabelWriteResult removed = await factory.WithWriterAsync(writer => writer.RemoveAsync("101", "ADA"));

        Assert.Equal(LabelWriteOutcome.AlreadyPresent, added.Outcome);
        Assert.Equal(LabelWriteOutcome.NotOwned, removed.Outcome);
        Assert.Equal(0, factory.Puts);
        Assert.Equal(["ada"], factory.Fake.Item("101").Labels);
        Assert.Empty(await factory.GetLedgerAsync());
    }

    [Fact]
    public async Task Owned_labels_match_without_regard_to_case()
    {
        using LabelApiFactory factory = new();
        await factory.LinkAsync();
        int ada = await factory.CreateProfileAsync("Ada");
        await factory.WithWriterAsync(writer => writer.AddAsync("101", "Ada", ada, "request"));
        factory.Fake.Item("101").SetLabels("ADA");

        LabelWriteResult again = await factory.WithWriterAsync(writer => writer.AddAsync("101", "ada", ada, "request"));
        LabelWriteResult removed = await factory.WithWriterAsync(writer => writer.RemoveAsync("101", "aDa"));

        Assert.Equal(LabelWriteOutcome.AlreadyPresent, again.Outcome);
        Assert.Equal(LabelWriteOutcome.Removed, removed.Outcome);
        Assert.Empty(factory.Fake.Item("101").Labels);
        Assert.Equal("ADA", factory.Fake.Requests.Last(request => request.Method == "PUT").Query["label[].tag.tag-"]);
        Assert.Empty(await factory.GetLedgerAsync());
    }

    [Fact]
    public async Task Removing_an_owned_label_that_is_already_gone_clears_the_ledger()
    {
        using LabelApiFactory factory = new();
        await factory.LinkAsync();
        int ada = await factory.CreateProfileAsync("Ada");
        await factory.WithWriterAsync(writer => writer.AddAsync("101", "Ada", ada, "request"));
        factory.Fake.Item("101").SetLabels();

        LabelWriteResult result = await factory.WithWriterAsync(writer => writer.RemoveAsync("101", "Ada"));

        Assert.Equal(LabelWriteOutcome.NotPresent, result.Outcome);
        Assert.Equal(1, factory.Puts);
        Assert.Empty(await factory.GetLedgerAsync());
    }

    [Fact]
    public async Task An_edit_made_in_plex_between_read_and_write_is_kept()
    {
        using LabelApiFactory factory = new();
        await factory.LinkAsync();
        int ada = await factory.CreateProfileAsync("Ada");
        factory.Fake.Item("101").SetLabels("Ben");
        factory.Fake.AfterRequest = request =>
        {
            if (request.Method == "GET" && request.Path == "/library/metadata/101" && factory.Fake.Count("GET", request.Path) == 2)
            {
                factory.Fake.Item("101").AddLabel("Favorites");
            }
        };

        LabelWriteResult result = await factory.WithWriterAsync(writer => writer.AddAsync("101", "Ada", ada, "request"));

        Assert.Equal(LabelWriteOutcome.Added, result.Outcome);
        Assert.Equal(["Ben", "Favorites", "Ada"], factory.Fake.Item("101").Labels);
        Assert.Equal(1, factory.Puts);
    }

    [Fact]
    public async Task A_stale_write_from_another_client_is_detected_and_both_edits_survive()
    {
        using LabelApiFactory factory = new();
        await factory.LinkAsync();
        int ada = await factory.CreateProfileAsync("Ada");
        factory.Fake.Item("101").SetLabels("Ben");
        factory.Fake.AfterRequest = request =>
        {
            if (request.Method == "PUT" && factory.Fake.Count("PUT", request.Path) == 1)
            {
                factory.Fake.Item("101").SetLabels("Ben", "Favorites");
            }
        };

        LabelWriteResult result = await factory.WithWriterAsync(writer => writer.AddAsync("101", "Ada", ada, "request"));

        Assert.Equal(LabelWriteOutcome.Added, result.Outcome);
        Assert.Equal(["Ben", "Favorites", "Ada"], factory.Fake.Item("101").Labels);
        Assert.Equal(2, factory.Puts);
        Assert.Single(await factory.GetLedgerAsync());
    }

    [Fact]
    public async Task Concurrent_writes_to_one_item_are_serialized()
    {
        using LabelApiFactory factory = new();
        await factory.LinkAsync();
        string[] names = ["Ada", "Ben", "Cy", "Dee", "Eve"];
        List<int> profiles = [];
        foreach (string name in names)
        {
            profiles.Add(await factory.CreateProfileAsync(name));
        }

        factory.Fake.MetadataDelay = TimeSpan.FromMilliseconds(20);

        LabelWriteResult[] results = await Task.WhenAll(names.Select((name, index) =>
            factory.WithWriterAsync(writer => writer.AddAsync("201", name, profiles[index], "request"))));

        Assert.All(results, result => Assert.Equal(LabelWriteOutcome.Added, result.Outcome));
        Assert.Equal(names.Order(StringComparer.Ordinal), factory.Fake.Item("201").Labels.Order(StringComparer.Ordinal));
        Assert.Equal(names.Length, factory.Puts);
        Assert.Equal(names.Length, (await factory.GetLedgerAsync()).Count);
    }

    [Fact]
    public async Task Writing_gives_up_when_plex_does_not_keep_the_change()
    {
        using LabelApiFactory factory = new();
        await factory.LinkAsync();
        int ada = await factory.CreateProfileAsync("Ada");
        factory.Fake.AfterRequest = request =>
        {
            if (request.Method == "PUT")
            {
                factory.Fake.Item("101").SetLabels();
            }
        };

        await Assert.ThrowsAsync<MediaServerException>(() =>
            factory.WithWriterAsync(writer => writer.AddAsync("101", "Ada", ada, "request")));

        Assert.Equal(LabelWriter.MaxAttempts, factory.Puts);
        Assert.Empty(await factory.GetLedgerAsync());
    }

    [Fact]
    public async Task A_deadline_after_plex_took_the_label_still_leaves_a_ledger_row()
    {
        using LabelApiFactory factory = new();
        await factory.LinkAsync();
        int ada = await factory.CreateProfileAsync("Ada");
        using CancellationTokenSource deadline = new();
        factory.Fake.AfterRequest = request =>
        {
            if (request.Method == "PUT")
            {
                deadline.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            factory.WithWriterAsync(writer => writer.AddAsync("101", "Ada", ada, "request", deadline.Token)));

        Assert.Equal(["Ada"], factory.Fake.Item("101").Labels);
        OwnedLabel owned = Assert.Single(await factory.GetLedgerAsync());
        Assert.Equal("Ada", owned.Label);
        Assert.Equal("101", owned.RatingKey);
    }

    [Fact]
    public async Task A_write_plex_rejected_leaves_no_ledger_row()
    {
        using LabelApiFactory factory = new();
        await factory.LinkAsync();
        int ada = await factory.CreateProfileAsync("Ada");
        factory.Fake.Delay = request => request.Method == "PUT"
            ? throw new InvalidOperationException("The server is down.")
            : Task.CompletedTask;

        await Assert.ThrowsAsync<MediaServerException>(() =>
            factory.WithWriterAsync(writer => writer.AddAsync("101", "Ada", ada, "request")));

        Assert.Empty(factory.Fake.Item("101").Labels);
        Assert.Empty(await factory.GetLedgerAsync());
    }

    [Fact]
    public async Task Unknown_items_and_invalid_input_are_rejected()
    {
        using LabelApiFactory factory = new();
        await factory.LinkAsync();
        int ada = await factory.CreateProfileAsync("Ada");

        LabelWriteResult missing = await factory.WithWriterAsync(writer => writer.AddAsync("999", "Ada", ada, "request"));

        Assert.Equal(LabelWriteOutcome.ItemNotFound, missing.Outcome);
        await Assert.ThrowsAsync<ArgumentException>(() => factory.WithWriterAsync(writer => writer.AddAsync("101", "a,b", ada, "request")));
        await Assert.ThrowsAsync<ArgumentException>(() => factory.WithWriterAsync(writer => writer.AddAsync("101", "Ada", ada + 100, "request")));
        await Assert.ThrowsAsync<ArgumentException>(() => factory.WithWriterAsync(writer => writer.AddAsync("101", "Ada", ada, "")));
        Assert.Equal(0, factory.Puts);
    }

    [Fact]
    public async Task Item_locks_serialize_one_key_and_leave_others_free()
    {
        ItemLocks locks = new();
        IDisposable first = await locks.AcquireAsync("a");

        Task<IDisposable> waiting = locks.AcquireAsync("a");
        IDisposable other = await locks.AcquireAsync("b").WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        bool blocked = !waiting.IsCompleted;
        first.Dispose();
        IDisposable second = await waiting.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(blocked);
        second.Dispose();
        other.Dispose();
    }
}
