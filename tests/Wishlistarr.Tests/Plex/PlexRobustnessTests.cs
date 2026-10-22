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
}
