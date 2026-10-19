using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Integrations.Tmdb;

namespace Wishlistarr.Tests.Tmdb;

public sealed class FixedKeyProvider : ITmdbApiKeyProvider
{
    public string? Key { get; set; } = FakeTmdbServer.ValidApiKey;

    public Task<string?> GetApiKeyAsync(CancellationToken ct = default) => Task.FromResult(this.Key);
}

public sealed class RecordingTimeProvider : TimeProvider
{
    private readonly List<TimeSpan> delays = [];

    public IReadOnlyList<TimeSpan> Delays
    {
        get
        {
            lock (this.delays)
            {
                return [.. this.delays];
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (this.delays)
        {
            this.delays.Add(dueTime);
        }

        return new ImmediateTimer(callback, state);
    }

    private sealed class ImmediateTimer : ITimer
    {
        public ImmediateTimer(TimerCallback callback, object? state) => Task.Run(() => callback(state));

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class TmdbHarness : IAsyncDisposable
{
    private readonly ServiceProvider provider;

    private TmdbHarness(FakeTmdbServer server, FixedKeyProvider keys, RecordingTimeProvider time, ServiceProvider provider)
    {
        this.Server = server;
        this.Keys = keys;
        this.Time = time;
        this.provider = provider;
    }

    public FakeTmdbServer Server { get; }

    public FixedKeyProvider Keys { get; }

    public RecordingTimeProvider Time { get; }

    public ITmdbClient Client => this.provider.GetRequiredService<ITmdbClient>();

    public TmdbImageProxy Images => this.provider.GetRequiredService<TmdbImageProxy>();

    public static async Task<TmdbHarness> CreateAsync(Action<TmdbOptions>? configure = null)
    {
        FakeTmdbServer server = await FakeTmdbServer.StartAsync();
        FixedKeyProvider keys = new();
        RecordingTimeProvider time = new();
        ServiceCollection services = new();
        services.AddTmdb<FixedKeyProvider>();
        services.AddSingleton<ITmdbApiKeyProvider>(keys);
        services.AddSingleton<TimeProvider>(time);
        services.Configure<TmdbOptions>(options =>
        {
            options.ApiBaseAddress = server.ApiBaseAddress;
            options.ImageBaseAddress = server.ImageBaseAddress;
            configure?.Invoke(options);
        });
        return new TmdbHarness(server, keys, time, services.BuildServiceProvider());
    }

    public async ValueTask DisposeAsync()
    {
        await this.provider.DisposeAsync();
        await this.Server.DisposeAsync();
    }
}
