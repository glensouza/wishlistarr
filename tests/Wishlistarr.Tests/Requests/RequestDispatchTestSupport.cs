using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Api.Requests;
using Wishlistarr.Core.Content;
using Wishlistarr.Core.Fulfilment;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Core.Requests;
using Wishlistarr.Data;
using Wishlistarr.Tests.Arr;
using Wishlistarr.Tests.Seerr;

namespace Wishlistarr.Tests.Requests;

public sealed class DispatchApiFactory : KidRequestApiFactory
{
    public const int MovieId = FakeArrServer.MovieTmdbId;
    public const int ShowId = FakeArrServer.ShowTmdbId;

    public DispatchApiFactory()
        : base(new FakeLibraryIndex())
    {
        this.Seerr = FakeSeerrServer.StartAsync().GetAwaiter().GetResult();
        this.Sonarr = FakeArrServer.StartAsync(ArrService.Sonarr).GetAwaiter().GetResult();
        this.Radarr = FakeArrServer.StartAsync(ArrService.Radarr).GetAwaiter().GetResult();
    }

    public FakeSeerrServer Seerr { get; }

    public FakeArrServer Sonarr { get; }

    public FakeArrServer Radarr { get; }

    public async Task<HttpClient> ConfiguredParentAsync(string? mode)
    {
        HttpClient parent = await this.CreateSignedInClientAsync();
        (await parent.PutAsJsonAsync("/api/v1/settings/seerr", new
        {
            url = this.Seerr.Address.ToString().TrimEnd('/'),
            apiKey = FakeSeerrServer.ApiKey,
            userId = FakeSeerrServer.DefaultUserId,
        })).EnsureSuccessStatusCode();
        await SaveArrAsync(parent, "sonarr", this.Sonarr);
        await SaveArrAsync(parent, "radarr", this.Radarr);
        if (mode is not null)
        {
            await this.SetModeAsync(parent, mode);
        }

        return parent;
    }

    public async Task SetModeAsync(HttpClient parent, string mode) =>
        Assert.Equal(HttpStatusCode.NoContent, (await parent.PutAsJsonAsync("/api/v1/settings/fulfilment.mode", new { value = mode })).StatusCode);

    public async Task<int> CreateRequestAsync(int profileId, MediaKind kind, int tmdbId)
    {
        using IServiceScope scope = this.Services.CreateScope();
        RequestResult created = await scope.ServiceProvider.GetRequiredService<MediaRequests>().CreateAsync(
            profileId,
            new TitleKey(kind, tmdbId),
            "Fixture title",
            RequestActor.Kid(profileId, "Ada"));
        Assert.Equal(RequestOutcome.Created, created.Outcome);
        return created.Request!.Id;
    }

    public async Task<RequestState> ApproveAsync(HttpClient parent, int id)
    {
        HttpResponseMessage response = await parent.PostAsJsonAsync("/api/v1/requests/approve", new { ids = new[] { id } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement item = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results")[0];
        Assert.Equal("succeeded", item.GetProperty("status").GetString());
        return Enum.Parse<RequestState>(item.GetProperty("state").GetString()!, ignoreCase: true);
    }

    public async Task<JsonElement> RetryAsync(HttpClient client, int id)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/requests/retry", new { ids = new[] { id } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results")[0];
    }

    public async Task RunDispatchJobAsync(HttpClient parent)
    {
        for (int attempt = 0; attempt < 1200; attempt++)
        {
            HttpResponseMessage response = await parent.PostAsync($"/api/v1/jobs/{RequestDispatchJob.JobName}/run", null);
            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                return;
            }

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            await Task.Delay(50);
        }

        throw new TimeoutException("The dispatch job never became free to run.");
    }

    public async Task<MediaRequest> WaitForAsync(int id, RequestState state)
    {
        for (int attempt = 0; attempt < 1200; attempt++)
        {
            MediaRequest request = (await this.GetRequestsAsync()).Single(row => row.Id == id);
            if (request.State == state)
            {
                return request;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Request {id} never became {state}.");
    }

    public async Task<MediaRequest> WaitForDispatchAsync(int id)
    {
        for (int attempt = 0; attempt < 1200; attempt++)
        {
            MediaRequest request = (await this.GetRequestsAsync()).Single(row => row.Id == id);
            if (request.DispatchedAt is not null && request.State == RequestState.Fulfilling)
            {
                return request;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Request {id} was never dispatched.");
    }

    public async Task<JsonElement> ListAsync(HttpClient parent, string state)
    {
        HttpResponseMessage response = await parent.GetAsync($"/api/v1/requests?state={state}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            this.Seerr.DisposeAsync().AsTask().GetAwaiter().GetResult();
            this.Sonarr.DisposeAsync().AsTask().GetAwaiter().GetResult();
            this.Radarr.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static async Task SaveArrAsync(HttpClient parent, string route, FakeArrServer fake) =>
        (await parent.PutAsJsonAsync($"/api/v1/settings/{route}", new
        {
            url = fake.Address.ToString().TrimEnd('/'),
            apiKey = FakeArrServer.ApiKey,
            qualityProfileId = 4,
            rootFolderPath = "/media/kids",
            tagIds = new[] { 1 },
        })).EnsureSuccessStatusCode();
}
