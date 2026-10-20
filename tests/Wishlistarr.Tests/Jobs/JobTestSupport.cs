using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Api.Jobs;
using Wishlistarr.Data;

namespace Wishlistarr.Tests.Jobs;

public sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset now = start;

    public void Advance(TimeSpan by) => this.now += by;

    public override DateTimeOffset GetUtcNow() => this.now;
}

public sealed class JobProbe
{
    private int active;
    private int maxActive;
    private int runs;
    private TaskCompletionSource started = NewSignal();
    private TaskCompletionSource release = NewSignal();

    public int MaxActive => this.maxActive;

    public int Runs => this.runs;

    public Exception? Failure { get; set; }

    public bool Block { get; set; } = true;

    public Task Started => this.started.Task;

    public void Reset()
    {
        this.started = NewSignal();
        this.release = NewSignal();
        this.Failure = null;
        this.Block = true;
    }

    public void Release() => this.release.TrySetResult();

    public async Task RunAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref this.runs);
        int current = Interlocked.Increment(ref this.active);
        int seen;
        do
        {
            seen = this.maxActive;
        }
        while (current > seen && Interlocked.CompareExchange(ref this.maxActive, current, seen) != seen);

        try
        {
            this.started.TrySetResult();
            if (this.Block)
            {
                await this.release.Task.WaitAsync(ct);
            }

            if (this.Failure is not null)
            {
                throw this.Failure;
            }
        }
        finally
        {
            Interlocked.Decrement(ref this.active);
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class ProbeJob(JobProbe probe) : IBackgroundJob
{
    public const string Name = "probe";

    public Task RunAsync(CancellationToken ct) => probe.RunAsync(ct);
}

public sealed class JobHarness : IAsyncDisposable
{
    private readonly string directory;

    private JobHarness(ServiceProvider provider, string directory)
    {
        this.Provider = provider;
        this.directory = directory;
    }

    public ServiceProvider Provider { get; }

    public JobProbe Probe => this.Provider.GetRequiredService<JobProbe>();

    public ManualTime Time => (ManualTime)this.Provider.GetRequiredService<TimeProvider>();

    public JobRunner Runner => this.Provider.GetRequiredService<JobRunner>();

    public static async Task<JobHarness> CreateAsync(
        TimeSpan? interval = null,
        IReadOnlyDictionary<string, string?>? settings = null)
    {
        string directory = Directory.CreateTempSubdirectory("wishlistarr-jobs").FullName;
        string database = Path.Combine(directory, "jobs.db");
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? new Dictionary<string, string?>())
            .Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new ManualTime(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        services.AddDbContext<WishlistarrDbContext>(options => options.UseWishlistarrSqlite(database));
        services.AddSingleton<JobProbe>();
        services.AddBackgroundJob<ProbeJob>(ProbeJob.Name, "Test job.", interval);
        services.AddOptions<JobOptions>().Bind(configuration.GetSection(JobOptions.SectionName));
        services.AddSingleton<JobRunner>();
        services.AddSingleton<JobScheduler>();
        services.AddScoped<JobHistoryCleanup>();

        ServiceProvider provider = services.BuildServiceProvider();
        using (IServiceScope scope = provider.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().Database.MigrateAsync();
        }

        return new JobHarness(provider, directory);
    }

    public async Task<List<JobRun>> GetRunsAsync()
    {
        using IServiceScope scope = this.Provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WishlistarrDbContext>().JobRuns
            .AsNoTracking()
            .OrderBy(run => run.Id)
            .ToListAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await this.Provider.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(this.directory, true);
    }
}
