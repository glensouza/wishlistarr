using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Wishlistarr.Api.Settings;
using Wishlistarr.Core.MediaServers;
using Wishlistarr.Integrations.Plex;

namespace Wishlistarr.Tests.Plex;

public sealed class PlexRobustnessTests
{
    [Fact]
    public async Task Parallel_link_checks_both_succeed_and_store_one_sign_in()
    {
        TokenSelectBarrier barrier = new();
        await using SqlitePlexHarness harness = await SqlitePlexHarness.CreateAsync(barrier);
        MediaServerLinkStart link = await harness.Account.StartLinkAsync();
        harness.Server.PinApproved = true;
        barrier.Armed = true;
        int arrived = 0;
        TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        string pinPath = "/api/v2/pins/" + link.Id;
        harness.Server.Delay = request =>
        {
            if (request.Method == "GET" && request.Path == pinPath)
            {
                if (Interlocked.Increment(ref arrived) == 2)
                {
                    released.TrySetResult();
                }

                return released.Task;
            }

            return Task.CompletedTask;
        };

        MediaServerLinkCheck?[] checks = await Task.WhenAll(
            harness.Account.CheckLinkAsync(link.Id),
            harness.Account.CheckLinkAsync(link.Id));

        Assert.True(barrier.Reads >= 2, $"reads={barrier.Reads} saw={barrier.Seen}");
        Assert.Equal(2, arrived);
        Assert.All(checks, check =>
        {
            Assert.Equal(MediaServerLinkState.Linked, check?.State);
            Assert.Equal(FakePlexServer.AccountName, check?.AccountName);
        });
        Assert.Null(await harness.Account.CheckLinkAsync(link.Id));
        Assert.Equal(1, await harness.CountAsync(PlexSettingKeys.Token));
        Assert.Equal(1, await harness.CountAsync(PlexSettingKeys.AccountId));
        Assert.Equal(1, await harness.CountAsync(PlexSettingKeys.AccountName));
        Assert.Equal(
            FakePlexServer.AccountToken,
            await harness.InScopeAsync(services => services.GetRequiredService<SettingsStore>().GetAsync(PlexSettingKeys.Token)));
    }

    [Fact]
    public async Task Server_url_and_token_are_not_visible_as_a_mix_of_old_and_new()
    {
        TornCommitHold hold = new();
        await using SqlitePlexHarness harness = await SqlitePlexHarness.CreateAsync(hold, hold.Transactions);
        await harness.LinkAsync();
        const string oldUrl = "http://198.51.100.8:32400/";
        await harness.InScopeAsync(async services =>
        {
            SettingsStore store = services.GetRequiredService<SettingsStore>();
            await store.SetAsync(PlexSettingKeys.ServerId, FakePlexServer.MachineIdentifier, false);
            await store.SetAsync(PlexSettingKeys.ServerUrl, oldUrl, false);
            await store.SetAsync(PlexSettingKeys.ServerToken, "old-token", true);
            return 0;
        });
        Dictionary<string, string> seeded = await harness.ReadRawAsync(PlexSettingKeys.ServerUrl, PlexSettingKeys.ServerToken);
        string oldToken = seeded[PlexSettingKeys.ServerToken];
        string newUrl = harness.Server.Address.ToString();
        hold.Armed = true;

        List<(string? Url, string? Token)> samples = [];
        Task<SelectedMediaServer?> selecting = harness.Account.SelectServerAsync(FakePlexServer.MachineIdentifier);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (!selecting.IsCompleted)
        {
            samples.Add(await ReadPairAsync(harness));
            if (hold.Torn.Task.IsCompleted)
            {
                hold.Observed.TrySetResult();
            }

            await Task.Delay(1, timeout.Token);
        }

        samples.Add(await ReadPairAsync(harness));
        if (hold.Torn.Task.IsCompleted)
        {
            hold.Observed.TrySetResult();
        }

        SelectedMediaServer? selected = await selecting;
        Assert.NotNull(selected);
        Assert.Equal(newUrl, selected.Connection.Uri.ToString());
        Assert.Contains(samples, pair => pair.Url == newUrl && pair.Token != oldToken);
        Assert.All(samples, pair => Assert.True(
            (pair.Url == oldUrl && pair.Token == oldToken) || (pair.Url == newUrl && pair.Token is not null && pair.Token != oldToken),
            $"Saw {pair.Url} with token {pair.Token}."));
    }

    [Fact]
    public async Task A_request_does_not_read_a_new_token_with_the_previous_address()
    {
        SplitServerRead split = new();
        await using SqlitePlexHarness harness = await SqlitePlexHarness.CreateAsync(split);
        await harness.LinkAndSelectAsync();
        split.OnSplit = async urlOnly =>
        {
            if (urlOnly)
            {
                await harness.InScopeAsync(async services =>
                {
                    await services.GetRequiredService<SettingsStore>().SetAsync(PlexSettingKeys.ServerToken, "wrong-token", true);
                    return 0;
                });
                return;
            }

            harness.Server.ResourcesJson = "[]";
            await harness.InScopeAsync(async services =>
            {
                await services.GetRequiredService<SettingsStore>().SetAsync(PlexSettingKeys.ServerUrl, harness.Server.RemoteHttpAddress.ToString(), false);
                return 0;
            });
        };
        split.Armed = true;
        int before = harness.Server.Requests.Count;

        MediaServerTestResult result = await harness.Media.TestConnectionAsync();

        Assert.True(result.Valid);
        FakePlexServer.RecordedRequest root = Assert.Single(harness.Server.Requests.Skip(before), request => request.Path == "/");
        Assert.Equal(FakePlexServer.ServerToken, root.Token);
    }

    private static async Task<(string? Url, string? Token)> ReadPairAsync(SqlitePlexHarness harness)
    {
        Dictionary<string, string> values = await harness.ReadRawAsync(PlexSettingKeys.ServerUrl, PlexSettingKeys.ServerToken);
        values.TryGetValue(PlexSettingKeys.ServerUrl, out string? url);
        values.TryGetValue(PlexSettingKeys.ServerToken, out string? token);
        return (url, token);
    }

    private sealed class TokenSelectBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource selected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> commands = [];
        private int reads;
        private int finished;

        public bool Armed { get; set; }

        public int Reads => this.reads;

        public string Seen => string.Join(" || ", this.commands);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (!this.Armed || !Includes(command, PlexSettingKeys.Token))
            {
                return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
            }

            lock (this.commands)
            {
                this.commands.Add(command.CommandText);
            }

            int read = Interlocked.Increment(ref this.reads);
            if (read == 1)
            {
                try
                {
                    await this.entered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                }
                catch (TimeoutException)
                {
                    throw new TimeoutException("The second plex.token read never started. Saw: " + string.Join(" || ", this.commands));
                }
            }
            else if (read == 2)
            {
                this.entered.TrySetResult();
            }

            InterceptionResult<DbDataReader> executed = await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
            if (read > 2)
            {
                return executed;
            }

            if (Interlocked.Increment(ref this.finished) == 1)
            {
                await this.selected.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            else
            {
                this.selected.TrySetResult();
            }

            return executed;
        }

        private static bool Includes(DbCommand command, string key)
        {
            if (!command.CommandText.Contains("Settings", StringComparison.Ordinal))
            {
                return false;
            }

            foreach (DbParameter parameter in command.Parameters)
            {
                if (parameter.Value is string value && value == key)
                {
                    return true;
                }
            }

            return false;
        }
    }

    private sealed class TornCommitHold : DbCommandInterceptor
    {
        private readonly object gate = new();
        private readonly Dictionary<DbConnection, HashSet<string>> keysByConnection = [];

        public TornCommitHold() => this.Transactions = new TransactionWatcher(this);

        public TransactionWatcher Transactions { get; }

        public bool Armed { get; set; }

        public TaskCompletionSource Torn { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (this.Armed && command.Connection is DbConnection connection && IsWrite(command))
            {
                HashSet<string> keys = this.Keys(connection);
                lock (keys)
                {
                    foreach (DbParameter parameter in command.Parameters)
                    {
                        if (parameter.Value is string value && value.StartsWith("plex.", StringComparison.Ordinal))
                        {
                            keys.Add(value);
                        }
                    }
                }
            }

            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public async Task OnCommittedAsync(DbTransaction transaction)
        {
            if (transaction.Connection is not DbConnection connection)
            {
                return;
            }

            HashSet<string>? keys;
            lock (this.gate)
            {
                this.keysByConnection.Remove(connection, out keys);
            }

            bool torn = false;
            if (keys is not null)
            {
                lock (keys)
                {
                    torn = this.Armed && keys.Contains(PlexSettingKeys.ServerToken) && !keys.Contains(PlexSettingKeys.ServerUrl);
                }
            }

            if (!torn)
            {
                return;
            }

            this.Torn.TrySetResult();
            await this.Observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        private HashSet<string> Keys(DbConnection connection)
        {
            lock (this.gate)
            {
                if (!this.keysByConnection.TryGetValue(connection, out HashSet<string>? keys))
                {
                    this.keysByConnection[connection] = keys = [];
                }

                return keys;
            }
        }

        private static bool IsWrite(DbCommand command) =>
            command.CommandText.Contains("INSERT", StringComparison.OrdinalIgnoreCase)
            || command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase);

        public sealed class TransactionWatcher(TornCommitHold owner) : DbTransactionInterceptor
        {
            public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default) =>
                owner.OnCommittedAsync(transaction);
        }
    }

    private sealed class SplitServerRead : DbCommandInterceptor
    {
        private int busy;

        public bool Armed { get; set; }

        public Func<bool, Task>? OnSplit { get; set; }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (this.Armed && this.busy == 0 && this.OnSplit is not null && IsSplit(command, out bool urlOnly) && Interlocked.Exchange(ref this.busy, 1) == 0)
            {
                await this.OnSplit(urlOnly);
            }

            return result;
        }

        private static bool IsSplit(DbCommand command, out bool urlOnly)
        {
            urlOnly = false;
            if (!command.CommandText.Contains("Settings", StringComparison.Ordinal))
            {
                return false;
            }

            bool url = false;
            bool token = false;
            foreach (DbParameter parameter in command.Parameters)
            {
                if (parameter.Value is not string value)
                {
                    continue;
                }

                url |= value == PlexSettingKeys.ServerUrl;
                token |= value == PlexSettingKeys.ServerToken;
            }

            if (url == token)
            {
                return false;
            }

            urlOnly = url;
            return true;
        }
    }
}
