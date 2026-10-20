using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Api.Jobs;
using Wishlistarr.Data;

namespace Wishlistarr.Tests.Jobs;

public sealed class JobApiFactory : AuthApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting($"Jobs:Schedules:{JobHistoryCleanup.JobName}:Enabled", "false");
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<JobProbe>();
            services.AddBackgroundJob<ProbeJob>(ProbeJob.Name, "Test job.", null);
        });
    }
}

public sealed class JobApiTests(JobApiFactory factory) : IClassFixture<JobApiFactory>
{
    [Fact]
    public async Task Manual_trigger_runs_the_job_and_records_the_run()
    {
        JobProbe probe = factory.Services.GetRequiredService<JobProbe>();
        JobRunner runner = factory.Services.GetRequiredService<JobRunner>();
        HttpClient client = await factory.CreateAuthorizedClientAsync();
        probe.Reset();

        HttpResponseMessage first = await client.PostAsync($"/api/v1/jobs/{ProbeJob.Name}/run", null);
        await probe.Started;
        HttpResponseMessage second = await client.PostAsync($"/api/v1/jobs/{ProbeJob.Name}/run", null);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("application/problem+json", second.Content.Headers.ContentType?.MediaType);

        probe.Release();
        while (runner.IsRunning(ProbeJob.Name))
        {
            await Task.Yield();
        }

        JsonElement runs = await client.GetFromJsonAsync<JsonElement>($"/api/v1/jobs/{ProbeJob.Name}/runs");
        JsonElement run = runs.EnumerateArray().First();
        Assert.Equal("manual", run.GetProperty("trigger").GetString());
        Assert.Equal("succeeded", run.GetProperty("outcome").GetString());
        Assert.NotNull(run.GetProperty("finishedAt").GetString());
        Assert.Equal(JsonValueKind.Null, run.GetProperty("error").ValueKind);
    }

    [Fact]
    public async Task Unknown_job_returns_not_found()
    {
        HttpClient client = await factory.CreateAuthorizedClientAsync();

        HttpResponseMessage trigger = await client.PostAsync("/api/v1/jobs/missing/run", null);
        HttpResponseMessage runs = await client.GetAsync("/api/v1/jobs/missing/runs");

        Assert.Equal(HttpStatusCode.NotFound, trigger.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, runs.StatusCode);
    }

    [Fact]
    public async Task Kid_cannot_list_or_trigger_jobs()
    {
        await factory.EnsureParentAsync();
        HttpClient kid = factory.CreateClient();
        kid.DefaultRequestHeaders.Add(AuthApiFactory.KidHeader, "1");

        HttpResponseMessage list = await kid.GetAsync("/api/v1/jobs");
        HttpResponseMessage trigger = await kid.PostAsync($"/api/v1/jobs/{ProbeJob.Name}/run", null);

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, trigger.StatusCode);
    }

    [Fact]
    public async Task System_status_lists_jobs_with_their_last_run()
    {
        JobProbe probe = factory.Services.GetRequiredService<JobProbe>();
        JobRunner runner = factory.Services.GetRequiredService<JobRunner>();
        HttpClient client = await factory.CreateAuthorizedClientAsync();
        probe.Reset();
        probe.Block = false;
        probe.Failure = new InvalidOperationException("status failure");
        JobStartResult result = await runner.StartAsync(ProbeJob.Name, JobRunTrigger.Manual);
        await result.Completion;

        JsonElement status = await client.GetFromJsonAsync<JsonElement>("/api/v1/system/status");

        JsonElement job = status.GetProperty("jobs").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == ProbeJob.Name);
        Assert.False(job.GetProperty("isRunning").GetBoolean());
        Assert.Equal(JsonValueKind.Null, job.GetProperty("intervalSeconds").ValueKind);
        JsonElement last = job.GetProperty("lastRun");
        Assert.Equal("failed", last.GetProperty("outcome").GetString());
        Assert.Equal("status failure", last.GetProperty("error").GetString());
        Assert.NotNull(last.GetProperty("startedAt").GetString());
        Assert.NotNull(last.GetProperty("finishedAt").GetString());
        Assert.Contains(
            status.GetProperty("jobs").EnumerateArray(),
            item => item.GetProperty("name").GetString() == JobHistoryCleanup.JobName
                && !item.GetProperty("enabled").GetBoolean());
    }
}
