using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wishlistarr.Api.Backup;
using Wishlistarr.Api.Jobs;
using Wishlistarr.Api.Notifications;
using Wishlistarr.Api.Requests;
using Wishlistarr.Api.Settings;
using Wishlistarr.Api.System;
using Wishlistarr.Core;
using Wishlistarr.Core.Content;
using Wishlistarr.Core.Library;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;
using Wishlistarr.Integrations.Tmdb;
using Wishlistarr.Tests.Backup;
using Wishlistarr.Tests.Requests;

namespace Wishlistarr.Tests.Notifications;

public sealed class WebPushFactory : AuthApiFactory
{
    public const int MovieId = 129;
    public const string VisibleTitle = "Spirited Away";
    public const string StoredTitle = "Stored secret title";

    public ManualTimeProvider Clock { get; } = new();

    public ScriptedTmdb Tmdb { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting($"Jobs:Schedules:{NotificationJob.JobName}:Enabled", "false");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(this.Clock);
            services.RemoveAll<ILibraryIndex>();
            services.AddSingleton<ILibraryIndex>(new FakeLibraryIndex());
            services.RemoveAll<ITmdbClient>();
            services.AddSingleton<ITmdbClient>(this.Tmdb);
        });
    }

    public async Task RunAsync()
    {
        JobStartResult run = await this.Services.GetRequiredService<JobRunner>().StartAsync(NotificationJob.JobName, JobRunTrigger.Manual);
        Assert.Equal(JobStartStatus.Started, run.Status);
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        JobRun? result = await this.Services.GetRequiredService<JobRunner>().GetLastRunAsync(NotificationJob.JobName);
        Assert.Equal(JobRunOutcome.Succeeded, result?.Outcome);
    }

    public sealed class ScriptedTmdb : ITmdbClient
    {
        public Task<bool> TestApiKeyAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<TmdbPage<TmdbSearchResult>> SearchAsync(string query, TmdbMediaType? type = null, int page = 1, string language = "en-US", CancellationToken ct = default) =>
            Task.FromResult(Empty());

        public Task<TmdbDetails> GetDetailsAsync(TmdbMediaType type, int id, string language = "en-US", CancellationToken ct = default) =>
            Task.FromResult(new TmdbDetails(
                id,
                id == MovieId ? VisibleTitle : "Other title",
                null,
                "A story.",
                null,
                null,
                null,
                "2001-07-20",
                null,
                125,
                null,
                "Released",
                [],
                8.5,
                "ja",
                false));

        public Task<IReadOnlyList<TmdbKeyword>> GetKeywordsAsync(TmdbMediaType type, int id, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbKeyword>>([]);

        public Task<TmdbCredits> GetCreditsAsync(TmdbMediaType type, int id, string language = "en-US", CancellationToken ct = default) =>
            Task.FromResult(new TmdbCredits([], []));

        public Task<TmdbCertification?> GetCertificationAsync(TmdbMediaType type, int id, IReadOnlyList<string> regions, CancellationToken ct = default) =>
            Task.FromResult<TmdbCertification?>(new TmdbCertification("US", id == MovieId ? "PG-13" : "G", []));

        public Task<TmdbVideos> GetVideosAsync(TmdbMediaType type, int id, string language = "en-US", CancellationToken ct = default) =>
            Task.FromResult(new TmdbVideos([]));

        public Task<TmdbImages> GetImagesAsync(TmdbMediaType type, int id, string imageLanguages = "en,null", CancellationToken ct = default) =>
            Task.FromResult(new TmdbImages([], [], []));

        public Task<TmdbPage<TmdbSearchResult>> DiscoverAsync(TmdbMediaType type, TmdbDiscoverQuery query, string language = "en-US", CancellationToken ct = default) =>
            Task.FromResult(Empty());

        public Task<TmdbPage<TmdbSearchResult>> GetTrendingAsync(TmdbMediaType type, string window = "week", int page = 1, string language = "en-US", CancellationToken ct = default) =>
            Task.FromResult(Empty());

        public Task<TmdbWatchProviders> GetWatchProvidersAsync(TmdbMediaType type, int id, CancellationToken ct = default) =>
            Task.FromResult(new TmdbWatchProviders(new Dictionary<string, TmdbRegionProviders>()));

        public Task<int?> FindIdAsync(TmdbMediaType type, ExternalIdSource source, string externalId, CancellationToken ct = default) =>
            Task.FromResult<int?>(null);

        public Task<IReadOnlyList<TmdbListedGenre>> GetGenresAsync(string language = "en-US", CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TmdbListedGenre>>([]);

        private static TmdbPage<TmdbSearchResult> Empty() => new(1, [], 0, 0);
    }
}

public sealed class WebPushTests
{
    [Fact]
    public async Task Parent_can_subscribe_a_browser_and_a_kid_cannot_see_or_change_it()
    {
        using WebPushFactory factory = new();
        using HttpClient parent = await factory.CreateSignedInClientAsync();
        (string endpoint, string p256dh, string auth) = Browser();
        HttpResponseMessage created = await parent.PostAsJsonAsync("/api/v1/notifications/web-push", new { endpoint, p256dh, auth });
        JsonElement saved = await created.Content.ReadFromJsonAsync<JsonElement>();
        int id = saved.GetProperty("id").GetInt32();
        string listed = await parent.GetStringAsync("/api/v1/notifications/web-push");
        string replacedAuth = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        HttpResponseMessage replaced = await parent.PostAsJsonAsync("/api/v1/notifications/web-push", new { endpoint, p256dh, auth = replacedAuth });

        using HttpClient kid = factory.CreateClient();
        kid.DefaultRequestHeaders.Add(AuthApiFactory.KidHeader, "1");
        HttpResponseMessage kidList = await kid.GetAsync("/api/v1/notifications/web-push");
        HttpResponseMessage kidSave = await kid.PostAsJsonAsync("/api/v1/notifications/web-push", new { endpoint, p256dh, auth });
        HttpResponseMessage kidDelete = await kid.DeleteAsync($"/api/v1/notifications/web-push/{id}");
        string afterKid = await parent.GetStringAsync("/api/v1/notifications/web-push");
        HttpResponseMessage deleted = await parent.DeleteAsync($"/api/v1/notifications/web-push/{id}");
        HttpResponseMessage missing = await parent.DeleteAsync($"/api/v1/notifications/web-push/{id}");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        Assert.Equal(id, (await replaced.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32());
        Assert.Contains("\"publicKey\":\"B", listed, StringComparison.Ordinal);
        Assert.Contains($"\"id\":{id}", listed, StringComparison.Ordinal);
        Assert.DoesNotContain(endpoint, listed, StringComparison.Ordinal);
        Assert.DoesNotContain(p256dh, listed, StringComparison.Ordinal);
        Assert.DoesNotContain(auth, listed, StringComparison.Ordinal);
        Assert.DoesNotContain(replacedAuth, listed, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, kidList.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, kidSave.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, kidDelete.StatusCode);
        Assert.DoesNotContain(endpoint, await kidSave.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains($"\"id\":{id}", afterKid, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(WebPushCatalog.NotFound, (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        Assert.DoesNotContain("subscriptions\":[{", await parent.GetStringAsync("/api/v1/notifications/web-push"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Paired_device_can_subscribe_and_an_unpaired_browser_cannot()
    {
        await using PushService service = await PushService.StartAsync();
        using WebPushFactory factory = new();
        using HttpClient parent = await factory.CreateSignedInClientAsync();
        int profile = await ProfileAsync(factory, parent, "Ada", 16);
        (HttpClient device, int deviceId) = await PairAsync(factory, parent, "Tablet", [profile]);
        using HttpClient unpaired = factory.CreateClient();
        PushSubscriber first = service.Add();
        PushSubscriber other = service.Add();
        HttpResponseMessage anonymous = await unpaired.PostAsJsonAsync("/api/v1/kid/web-push", Subscription(first));
        HttpResponseMessage created = await device.PostAsJsonAsync("/api/v1/kid/web-push", Subscription(first));
        string state = await device.GetStringAsync("/api/v1/kid/web-push");
        (HttpClient second, _) = await PairAsync(factory, parent, "Other", [profile]);
        HttpResponseMessage conflict = await second.PostAsJsonAsync("/api/v1/kid/web-push", Subscription(first));
        HttpResponseMessage replaced = await device.PostAsJsonAsync("/api/v1/kid/web-push", Subscription(other));
        HttpResponseMessage removed = await device.DeleteAsync("/api/v1/kid/web-push");
        HttpResponseMessage again = await device.DeleteAsync("/api/v1/kid/web-push");
        JsonElement after = await device.GetFromJsonAsync<JsonElement>("/api/v1/kid/web-push");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Contains("\"subscribed\":true", state, StringComparison.Ordinal);
        Assert.DoesNotContain(first.Endpoint.AbsoluteUri, state, StringComparison.Ordinal);
        Assert.DoesNotContain(first.PublicKey, state, StringComparison.Ordinal);
        Assert.DoesNotContain(first.Auth, state, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(WebPushCatalog.AlreadySubscribed, (await conflict.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.Created, replaced.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.False(after.GetProperty("subscribed").GetBoolean());
        Assert.Equal(0, await CountAsync(factory, deviceId));
    }

    [Fact]
    public async Task Ready_notice_is_delivered_encrypted_and_a_later_label_does_not_send_it_again()
    {
        await using PushService service = await PushService.StartAsync();
        using WebPushFactory factory = new();
        using HttpClient parent = await factory.CreateSignedInClientAsync();
        service.ApplicationServerKey = await PublicKeyAsync(parent);
        int profile = await ProfileAsync(factory, parent, "Ada", 16);
        (HttpClient device, _) = await PairAsync(factory, parent, "Tablet", [profile]);
        PushSubscriber subscriber = service.Add();
        Assert.Equal(HttpStatusCode.Created, (await device.PostAsJsonAsync("/api/v1/kid/web-push", Subscription(subscriber))).StatusCode);
        int requestId = await ReadyAsync(factory, profile);

        await factory.RunAsync();
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<MediaRequests>().TransitionAsync(requestId, RequestState.Labeled, RequestActor.System("web-push-test"));
        }

        await factory.RunAsync();

        PushDelivery delivery = Assert.Single(service.Messages);
        Assert.Equal("86400", delivery.Ttl);
        Assert.Equal("aes128gcm", delivery.ContentEncoding);
        Assert.True(string.IsNullOrEmpty(delivery.ContentType));
        Assert.StartsWith("vapid t=", delivery.Authorization, StringComparison.Ordinal);
        Assert.Contains($",k={service.ApplicationServerKey}", delivery.Authorization, StringComparison.Ordinal);
        Assert.DoesNotContain(Encoding.UTF8.GetBytes(WebPushFactory.VisibleTitle), delivery.Body);
        using JsonDocument payload = JsonDocument.Parse(delivery.Plaintext!);
        Assert.Equal(WebPushFactory.VisibleTitle, payload.RootElement.GetProperty("title").GetString());
        Assert.Equal($"{WebPushFactory.VisibleTitle} is ready to watch.", payload.RootElement.GetProperty("body").GetString());
        Assert.DoesNotContain(WebPushFactory.StoredTitle, delivery.Plaintext, StringComparison.Ordinal);
        WebPushDelivery stored = Assert.Single(await DeliveriesAsync(factory));
        Assert.Null(stored.Message);
        Assert.Null(stored.LastError);
        Assert.NotNull(stored.CompletedAt);
    }

    [Theory]
    [InlineData(StatusCodes.Status404NotFound)]
    [InlineData(StatusCodes.Status410Gone)]
    public async Task Gone_subscription_is_removed(int status)
    {
        await using PushService service = await PushService.StartAsync();
        service.ForceStatus = status;
        using WebPushFactory factory = new();
        using HttpClient parent = await factory.CreateSignedInClientAsync();
        service.ApplicationServerKey = await PublicKeyAsync(parent);
        int profile = await ProfileAsync(factory, parent, "Ada", 16);
        (HttpClient device, int deviceId) = await PairAsync(factory, parent, "Tablet", [profile]);
        PushSubscriber subscriber = service.Add();
        Assert.Equal(HttpStatusCode.Created, (await device.PostAsJsonAsync("/api/v1/kid/web-push", Subscription(subscriber))).StatusCode);
        await ReadyAsync(factory, profile);

        await factory.RunAsync();

        PushDelivery delivery = Assert.Single(service.Messages);
        Assert.Contains(WebPushFactory.VisibleTitle, delivery.Plaintext, StringComparison.Ordinal);
        Assert.False((await device.GetFromJsonAsync<JsonElement>("/api/v1/kid/web-push")).GetProperty("subscribed").GetBoolean());
        Assert.Equal(0, await CountAsync(factory, deviceId));
        Assert.Empty(await DeliveriesAsync(factory));
    }

    [Fact]
    public async Task Rate_limit_waits_for_retry_after_before_trying_again()
    {
        await using PushService service = await PushService.StartAsync();
        service.ForceStatus = StatusCodes.Status429TooManyRequests;
        service.RetryAfterSeconds = 300;
        using WebPushFactory factory = new();
        using HttpClient parent = await factory.CreateSignedInClientAsync();
        service.ApplicationServerKey = await PublicKeyAsync(parent);
        int profile = await ProfileAsync(factory, parent, "Ada", 16);
        (HttpClient device, _) = await PairAsync(factory, parent, "Tablet", [profile]);
        Assert.Equal(HttpStatusCode.Created, (await device.PostAsJsonAsync("/api/v1/kid/web-push", Subscription(service.Add()))).StatusCode);
        await ReadyAsync(factory, profile);

        await factory.RunAsync();
        WebPushDelivery waiting = Assert.Single(await DeliveriesAsync(factory));
        Assert.Equal(1, waiting.Attempts);
        Assert.Contains("429", waiting.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain(WebPushFactory.VisibleTitle, waiting.LastError, StringComparison.Ordinal);
        Assert.Null(waiting.Message);
        factory.Clock.Advance(TimeSpan.FromMinutes(4));
        await factory.RunAsync();
        Assert.Single(service.Messages);
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await factory.RunAsync();

        Assert.Equal(2, service.Messages.Count);
        Assert.Equal(2, Assert.Single(await DeliveriesAsync(factory)).Attempts);
        Assert.True((await device.GetFromJsonAsync<JsonElement>("/api/v1/kid/web-push")).GetProperty("subscribed").GetBoolean());
    }

    [Fact]
    public async Task Rejected_vapid_is_retried_and_a_tampered_signature_is_refused()
    {
        await using PushService service = await PushService.StartAsync();
        using WebPushFactory factory = new();
        using IServiceScope scope = factory.Services.CreateScope();
        VapidKeys keys = scope.ServiceProvider.GetRequiredService<VapidKeys>();
        string publicKey = await keys.PublicKeyAsync();
        service.ApplicationServerKey = publicKey;
        PushSubscriber subscriber = service.Add();
        string authorization = await keys.CreateAuthorizationAsync(subscriber.Endpoint);
        using HttpClient direct = new();
        using HttpRequestMessage valid = new(HttpMethod.Post, subscriber.Endpoint);
        valid.Headers.TryAddWithoutValidation("TTL", "60");
        valid.Headers.TryAddWithoutValidation("Authorization", authorization);
        valid.Content = new ByteArrayContent([]);
        using HttpRequestMessage tampered = new(HttpMethod.Post, subscriber.Endpoint);
        tampered.Headers.TryAddWithoutValidation("TTL", "60");
        tampered.Headers.TryAddWithoutValidation("Authorization", TamperSignature(authorization));
        tampered.Content = new ByteArrayContent([]);
        using HttpRequestMessage missingTtl = new(HttpMethod.Post, subscriber.Endpoint);
        missingTtl.Headers.TryAddWithoutValidation("Authorization", authorization);
        missingTtl.Content = new ByteArrayContent([]);

        Assert.Equal(HttpStatusCode.Created, (await direct.SendAsync(valid)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await direct.SendAsync(tampered)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await direct.SendAsync(missingTtl)).StatusCode);

        service.ForceStatus = StatusCodes.Status401Unauthorized;
        using HttpClient parent = await factory.CreateSignedInClientAsync();
        int profile = await ProfileAsync(factory, parent, "Ada", 16);
        (HttpClient device, int deviceId) = await PairAsync(factory, parent, "Tablet", [profile]);
        Assert.Equal(HttpStatusCode.Created, (await device.PostAsJsonAsync("/api/v1/kid/web-push", Subscription(subscriber))).StatusCode);
        await ReadyAsync(factory, profile);
        await factory.RunAsync();
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await factory.RunAsync();

        Assert.Equal(3, service.Messages.Count);
        Assert.All(service.Messages.Skip(1), delivery =>
        {
            Assert.Contains(WebPushFactory.VisibleTitle, delivery.Plaintext, StringComparison.Ordinal);
            Assert.DoesNotContain(WebPushFactory.StoredTitle, delivery.Plaintext, StringComparison.Ordinal);
        });
        Assert.Equal(2, Assert.Single(await DeliveriesAsync(factory)).Attempts);
        Assert.Contains("401", Assert.Single(await DeliveriesAsync(factory)).LastError, StringComparison.Ordinal);
        Assert.Equal(1, await CountAsync(factory, deviceId));
    }

    [Fact]
    public async Task Notices_omit_a_title_a_profile_on_the_device_cannot_see()
    {
        await using PushService service = await PushService.StartAsync();
        using WebPushFactory factory = new();
        using HttpClient parent = await factory.CreateSignedInClientAsync();
        service.ApplicationServerKey = await PublicKeyAsync(parent);
        int younger = await ProfileAsync(factory, parent, "Bo", 8, "1111");
        int older = await ProfileAsync(factory, parent, "Ada", 16, "2222");
        (HttpClient shared, _) = await PairAsync(factory, parent, "Shared", [younger, older]);
        (HttpClient olderOnly, _) = await PairAsync(factory, parent, "Ada tablet", [older]);
        PushSubscriber sharedPush = service.Add();
        PushSubscriber olderPush = service.Add();
        Assert.Equal(HttpStatusCode.Created, (await shared.PostAsJsonAsync("/api/v1/kid/web-push", Subscription(sharedPush))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await olderOnly.PostAsJsonAsync("/api/v1/kid/web-push", Subscription(olderPush))).StatusCode);
        await ReadyAsync(factory, older);
        await ReadyAsync(factory, younger);

        await factory.RunAsync();

        PushDelivery olderNotice = Assert.Single(service.Messages, message => message.Path == olderPush.Endpoint.AbsolutePath);
        using JsonDocument olderPayload = JsonDocument.Parse(olderNotice.Plaintext!);
        Assert.Equal(WebPushFactory.VisibleTitle, olderPayload.RootElement.GetProperty("title").GetString());
        Assert.DoesNotContain(WebPushFactory.StoredTitle, olderNotice.Plaintext, StringComparison.Ordinal);
        Assert.Equal(2, service.Messages.Count(message => message.Path == sharedPush.Endpoint.AbsolutePath));
        Assert.All(
            service.Messages.Where(message => message.Path == sharedPush.Endpoint.AbsolutePath),
            message =>
            {
                using JsonDocument payload = JsonDocument.Parse(message.Plaintext!);
                Assert.Equal(WebPushNotices.GenericTitle, payload.RootElement.GetProperty("title").GetString());
                Assert.Equal(WebPushNotices.GenericBody, payload.RootElement.GetProperty("body").GetString());
                Assert.DoesNotContain(WebPushFactory.VisibleTitle, message.Plaintext, StringComparison.Ordinal);
                Assert.DoesNotContain(WebPushFactory.StoredTitle, message.Plaintext, StringComparison.Ordinal);
            });
        Assert.All(await DeliveriesAsync(factory), delivery =>
        {
            Assert.Null(delivery.Message);
            Assert.DoesNotContain(WebPushFactory.VisibleTitle, delivery.LastError ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(WebPushFactory.StoredTitle, delivery.LastError ?? string.Empty, StringComparison.Ordinal);
        });

        string olderDecision = await PreviewAsync(parent, older, WebPushFactory.MovieId);
        string youngerDecision = await PreviewAsync(parent, younger, WebPushFactory.MovieId);
        Assert.Equal(HttpStatusCode.OK, (await KidApiFactory.SignInAsync(shared, older, "2222")).StatusCode);
        JsonElement olderInApp = Assert.Single(await shared.GetFromJsonAsync<List<JsonElement>>("/api/v1/kid/ready-notices") ?? []);
        Assert.Equal(olderDecision == "allowed", olderInApp.GetProperty("title").ValueKind != JsonValueKind.Null);
        Assert.Equal(WebPushFactory.VisibleTitle, olderInApp.GetProperty("title").GetString());
        await AuthApiFactory.AddAntiforgeryTokenAsync(shared);
        Assert.Equal(HttpStatusCode.NoContent, (await shared.PostAsync($"/api/v1/kid/ready-notices/{olderInApp.GetProperty("id").GetInt32()}/dismiss", null)).StatusCode);
        Assert.Empty(await shared.GetFromJsonAsync<List<JsonElement>>("/api/v1/kid/ready-notices") ?? []);

        Assert.Equal(HttpStatusCode.OK, (await KidApiFactory.SignInAsync(shared, younger, "1111")).StatusCode);
        JsonElement youngerInApp = Assert.Single(await shared.GetFromJsonAsync<List<JsonElement>>("/api/v1/kid/ready-notices") ?? []);
        Assert.Equal(youngerDecision == "allowed", youngerInApp.GetProperty("title").ValueKind != JsonValueKind.Null);
        Assert.Equal(JsonValueKind.Null, youngerInApp.GetProperty("title").ValueKind);
        string youngerBody = await shared.GetStringAsync("/api/v1/kid/ready-notices");
        Assert.DoesNotContain(WebPushFactory.VisibleTitle, youngerBody, StringComparison.Ordinal);
        Assert.DoesNotContain(WebPushFactory.StoredTitle, youngerBody, StringComparison.Ordinal);
        Assert.Equal("allowed", olderDecision);
        Assert.Equal("blocked", youngerDecision);
    }

    [Fact]
    public async Task Revoke_repair_and_removing_a_profile_stop_ready_notices()
    {
        await using PushService service = await PushService.StartAsync();
        using WebPushFactory factory = new();
        using HttpClient parent = await factory.CreateSignedInClientAsync();
        service.ApplicationServerKey = await PublicKeyAsync(parent);
        int profile = await ProfileAsync(factory, parent, "Ada", 16);
        (HttpClient device, int deviceId) = await PairAsync(factory, parent, "Tablet", [profile]);
        PushSubscriber subscriber = service.Add();
        Assert.Equal(HttpStatusCode.Created, (await device.PostAsJsonAsync("/api/v1/kid/web-push", Subscription(subscriber))).StatusCode);
        await ReadyAsync(factory, profile);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<WebPushNotices>().CollectAsync(CancellationToken.None);
        }

        Assert.Equal(HttpStatusCode.OK, (await parent.PutAsJsonAsync($"/api/v1/kid-devices/{deviceId}", new { name = "Tablet", profileIds = Array.Empty<int>() })).StatusCode);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<WebPushNotices>().DeliverAsync(CancellationToken.None);
        }

        WebPushDelivery cancelled = Assert.Single(await DeliveriesAsync(factory));
        Assert.Equal(0, cancelled.Attempts);
        Assert.NotNull(cancelled.CompletedAt);
        Assert.Contains("changed", cancelled.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain(WebPushFactory.StoredTitle, cancelled.LastError, StringComparison.Ordinal);
        Assert.Empty(service.Messages);

        Assert.Equal(HttpStatusCode.OK, (await parent.PutAsJsonAsync($"/api/v1/kid-devices/{deviceId}", new { name = "Tablet", profileIds = new[] { profile } })).StatusCode);
        HttpResponseMessage reissued = await parent.PostAsync($"/api/v1/kid-devices/{deviceId}/pairing-code", null);
        Assert.Equal(HttpStatusCode.OK, reissued.StatusCode);
        JsonElement code = await reissued.Content.ReadFromJsonAsync<JsonElement>();
        using HttpClient repaired = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await repaired.PostAsJsonAsync("/api/v1/kid/pair", new { code = code.GetProperty("code").GetString() })).StatusCode);
        Assert.False((await repaired.GetFromJsonAsync<JsonElement>("/api/v1/kid/web-push")).GetProperty("subscribed").GetBoolean());
        Assert.Equal(0, await CountAsync(factory, deviceId));
        await ReadyAsync(factory, profile, 131);
        await factory.RunAsync();
        Assert.Empty(service.Messages);

        Assert.Equal(HttpStatusCode.Created, (await repaired.PostAsJsonAsync("/api/v1/kid/web-push", Subscription(service.Add()))).StatusCode);
        await ReadyAsync(factory, profile, 132);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<WebPushNotices>().CollectAsync(CancellationToken.None);
        }

        Assert.Equal(HttpStatusCode.NoContent, (await parent.DeleteAsync($"/api/v1/kid-devices/{deviceId}")).StatusCode);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<WebPushNotices>().DeliverAsync(CancellationToken.None);
        }

        Assert.Empty(service.Messages);
        Assert.Equal(0, await CountAsync(factory, deviceId));
    }

    [Fact]
    public async Task Vapid_key_survives_backup_and_is_not_returned_or_logged()
    {
        string source = BackupTestSupport.TempDirectory();
        string restoredPath = BackupTestSupport.TempDirectory();
        try
        {
            string publicKey;
            string secret;
            byte[] zip;
            await using (OwnedDataApiFactory sourceFactory = new(source))
            {
                using HttpClient parent = await sourceFactory.CreateAuthorizedClientAsync();
                publicKey = await PublicKeyAsync(parent);
                using (IServiceScope scope = sourceFactory.Services.CreateScope())
                {
                    secret = await scope.ServiceProvider.GetRequiredService<SettingsStore>().GetAsync(VapidKeys.SettingKey)
                        ?? throw new InvalidOperationException("The signing key was not saved.");
                }

                string body = await parent.GetStringAsync("/api/v1/notifications/web-push");
                Assert.Contains(publicKey, body, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
                LogBuffer logs = sourceFactory.Services.GetRequiredService<LogBuffer>();
                Assert.DoesNotContain(logs.List(null), entry =>
                    entry.Message.Contains(secret, StringComparison.Ordinal) || entry.Category.Contains(secret, StringComparison.Ordinal));
                AssertSecretAbsent(Path.Combine(source, DataDirectory.DatabaseFileName), secret);
                JobRunner runner = sourceFactory.Services.GetRequiredService<JobRunner>();
                JobStartResult backup = await runner.StartAsync(DatabaseBackupJob.JobName, JobRunTrigger.Manual);
                Assert.Equal(JobStartStatus.Started, backup.Status);
                await backup.Completion;
                Assert.Equal(JobRunOutcome.Succeeded, (await runner.GetLastRunAsync(DatabaseBackupJob.JobName))!.Outcome);
                zip = await File.ReadAllBytesAsync(Directory.GetFiles(Path.Combine(source, "backups"), "*.zip").Single());
                AssertSecretAbsent(zip, secret);
            }

            SqliteConnection.ClearAllPools();
            await using (OwnedDataApiFactory target = new(restoredPath))
            {
                using HttpClient client = await target.CreateAuthorizedClientAsync();
                using MultipartFormDataContent content = new();
                ByteArrayContent file = new(zip);
                file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                content.Add(file, "file", "backup.zip");
                Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/api/v1/backups/restore", content)).StatusCode);
            }

            SqliteConnection.ClearAllPools();
            await using OwnedDataApiFactory restarted = new(restoredPath);
            using HttpClient restored = await restarted.CreateAuthorizedClientAsync();
            Assert.Equal(publicKey, await PublicKeyAsync(restored));
            Assert.DoesNotContain(secret, await restored.GetStringAsync("/api/v1/notifications/web-push"), StringComparison.Ordinal);
            using IServiceScope restoredScope = restarted.Services.CreateScope();
            string authorization = await restoredScope.ServiceProvider.GetRequiredService<VapidKeys>().CreateAuthorizationAsync(new Uri("https://push.example/send"));
            Assert.DoesNotContain(secret, authorization, StringComparison.Ordinal);
            AssertSignedBy(authorization, publicKey, "https://push.example");
            AssertSecretAbsent(Path.Combine(restoredPath, DataDirectory.DatabaseFileName), secret);
        }
        finally
        {
            BackupTestSupport.DeleteDirectory(source);
            BackupTestSupport.DeleteDirectory(restoredPath);
        }
    }

    private static async Task<string> PublicKeyAsync(HttpClient parent)
    {
        JsonElement body = await parent.GetFromJsonAsync<JsonElement>("/api/v1/notifications/web-push");
        string? key = body.GetProperty("publicKey").GetString();
        Assert.NotNull(key);
        Assert.StartsWith("B", key, StringComparison.Ordinal);
        return key;
    }

    private static async Task<int> ProfileAsync(WebPushFactory factory, HttpClient parent, string name, int movieAgeLimit, string? pin = null)
    {
        HttpResponseMessage response = await parent.PostAsJsonAsync(
            "/api/v1/kid-profiles",
            new { name, avatar = "fox", pin, movieAgeLimit, plexLabel = name });
        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private static async Task<(HttpClient Device, int DeviceId)> PairAsync(WebPushFactory factory, HttpClient parent, string name, int[] profileIds)
    {
        HttpResponseMessage created = await parent.PostAsJsonAsync("/api/v1/kid-devices", new { name, profileIds });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        JsonElement body = await created.Content.ReadFromJsonAsync<JsonElement>();
        HttpClient device = factory.CreateClient();
        HttpResponseMessage paired = await device.PostAsJsonAsync("/api/v1/kid/pair", new { code = body.GetProperty("code").GetString() });
        Assert.Equal(HttpStatusCode.OK, paired.StatusCode);
        return (device, body.GetProperty("device").GetProperty("id").GetInt32());
    }

    private static async Task<int> ReadyAsync(WebPushFactory factory, int profileId, int tmdbId = WebPushFactory.MovieId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        MediaRequests requests = scope.ServiceProvider.GetRequiredService<MediaRequests>();
        RequestResult created = await requests.CreateAsync(
            profileId,
            new TitleKey(MediaKind.Movie, tmdbId),
            WebPushFactory.StoredTitle,
            RequestActor.Kid(profileId, "Kid"));
        Assert.Equal(RequestOutcome.Created, created.Outcome);
        int id = created.Request!.Id;
        Assert.Equal(RequestOutcome.Changed, (await requests.TransitionAsync(id, RequestState.Approved, RequestActor.Parent(1, "Parent"))).Outcome);
        Assert.Equal(RequestOutcome.Changed, (await requests.TransitionAsync(id, RequestState.Available, RequestActor.System("web-push-test"))).Outcome);
        return id;
    }

    private static async Task<string> PreviewAsync(HttpClient parent, int profileId, int tmdbId)
    {
        HttpResponseMessage response = await parent.PostAsJsonAsync($"/api/v1/kid-profiles/{profileId}/preview", new { kind = "movie", tmdbId });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("decision").GetString()!;
    }

    private static async Task<List<WebPushDelivery>> DeliveriesAsync(WebPushFactory factory)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().WebPushDeliveries.AsNoTracking().ToListAsync();
    }

    private static async Task<int> CountAsync(WebPushFactory factory, int deviceId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().WebPushSubscriptions.CountAsync(row => row.KidDeviceId == deviceId);
    }

    private static object Subscription(PushSubscriber subscriber) => new
    {
        endpoint = subscriber.Endpoint.AbsoluteUri,
        p256dh = subscriber.PublicKey,
        auth = subscriber.Auth,
    };

    private static (string Endpoint, string P256dh, string Auth) Browser()
    {
        using ECDiffieHellman key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return (
            "http://127.0.0.1/push/parent",
            Base64Url.EncodeToString(WebPushEncryption.Uncompressed(key.ExportParameters(false).Q)),
            Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16)));
    }

    private static string TamperSignature(string authorization)
    {
        int keyIndex = authorization.IndexOf(",k=", StringComparison.Ordinal);
        string token = authorization["vapid t=".Length..keyIndex];
        string[] parts = token.Split('.');
        char last = parts[2][^1];
        parts[2] = string.Concat(parts[2].AsSpan(0, parts[2].Length - 1), last == 'A' ? "B" : "A");
        return string.Concat("vapid t=", parts[0], ".", parts[1], ".", parts[2], authorization[keyIndex..]);
    }

    private static void AssertSignedBy(string authorization, string publicKey, string audience)
    {
        int keyIndex = authorization.IndexOf(",k=", StringComparison.Ordinal);
        Assert.True(keyIndex > "vapid t=".Length);
        Assert.Equal(publicKey, authorization[(keyIndex + 3)..]);
        string[] parts = authorization["vapid t=".Length..keyIndex].Split('.');
        Assert.Equal(3, parts.Length);
        Assert.True(WebPushEncryption.TryPublic(Base64Url.DecodeFromChars(publicKey), out ECParameters parameters));
        using ECDsa ecdsa = ECDsa.Create(parameters);
        byte[] signed = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{parts[0]}.{parts[1]}"));
        Assert.True(ecdsa.VerifyData(signed, Base64Url.DecodeFromChars(parts[2]), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        using JsonDocument claims = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
        Assert.Equal(audience, claims.RootElement.GetProperty("aud").GetString());
    }

    private static void AssertSecretAbsent(string path, string secret)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        AssertSecretAbsent(copy.ToArray(), secret);
        string wal = path + "-wal";
        if (File.Exists(wal))
        {
            AssertSecretAbsent(wal, secret);
        }
    }

    private static void AssertSecretAbsent(byte[] data, string secret)
    {
        Assert.DoesNotContain(secret, Encoding.UTF8.GetString(data), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, Encoding.Latin1.GetString(data), StringComparison.Ordinal);
    }
}
