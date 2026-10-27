using Microsoft.EntityFrameworkCore;

namespace Wishlistarr.Data;

public sealed class WishlistarrDbContext(DbContextOptions<WishlistarrDbContext> options) : DbContext(options)
{
    public DbSet<Setting> Settings => this.Set<Setting>();

    public DbSet<NotificationSubscription> NotificationSubscriptions => this.Set<NotificationSubscription>();

    public DbSet<NotificationDelivery> NotificationDeliveries => this.Set<NotificationDelivery>();

    public DbSet<NotificationApproval> NotificationApprovals => this.Set<NotificationApproval>();

    public DbSet<ParentAccount> ParentAccounts => this.Set<ParentAccount>();

    public DbSet<ParentSession> ParentSessions => this.Set<ParentSession>();

    public DbSet<ProfileTitleRule> ProfileTitleRules => this.Set<ProfileTitleRule>();

    public DbSet<KidProfile> KidProfiles => this.Set<KidProfile>();

    public DbSet<KidSession> KidSessions => this.Set<KidSession>();

    public DbSet<KidPinAttempt> KidPinAttempts => this.Set<KidPinAttempt>();

    public DbSet<KidDevice> KidDevices => this.Set<KidDevice>();

    public DbSet<KidDeviceProfile> KidDeviceProfiles => this.Set<KidDeviceProfile>();

    public DbSet<KidPairingCode> KidPairingCodes => this.Set<KidPairingCode>();

    public DbSet<OwnedLabel> OwnedLabels => this.Set<OwnedLabel>();

    public DbSet<MediaRequest> MediaRequests => this.Set<MediaRequest>();

    public DbSet<MediaRequestEvent> MediaRequestEvents => this.Set<MediaRequestEvent>();

    public DbSet<JobRun> JobRuns => this.Set<JobRun>();

    public DbSet<WebhookEvent> WebhookEvents => this.Set<WebhookEvent>();

    public DbSet<LibraryItem> LibraryItems => this.Set<LibraryItem>();

    public DbSet<LibraryTitle> LibraryTitles => this.Set<LibraryTitle>();

    public DbSet<LibraryExternalId> LibraryExternalIds => this.Set<LibraryExternalId>();

    public DbSet<LibraryItemLabel> LibraryItemLabels => this.Set<LibraryItemLabel>();

    public DbSet<LibraryRefreshState> LibraryRefreshStates => this.Set<LibraryRefreshState>();

    public DbSet<LabelGuardianReport> LabelGuardianReports => this.Set<LabelGuardianReport>();

    public DbSet<LabelGuardianEntry> LabelGuardianEntries => this.Set<LabelGuardianEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NotificationSubscription>(entity =>
        {
            entity.Property(row => row.Provider).HasMaxLength(16);
            entity.HasIndex(row => new { row.ParentAccountId, row.Provider }).IsUnique();
            entity.HasOne<ParentAccount>().WithMany().HasForeignKey(row => row.ParentAccountId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<NotificationDelivery>(entity =>
        {
            entity.Property(row => row.Event).HasMaxLength(32);
            entity.Property(row => row.Message).HasMaxLength(1000);
            entity.Property(row => row.LastError).HasMaxLength(200);
            entity.Property(row => row.NextAttemptAt).HasConversion(value => value.ToUnixTimeMilliseconds(), value => DateTimeOffset.FromUnixTimeMilliseconds(value));
            entity.HasIndex(row => new { row.NotificationSubscriptionId, row.MediaRequestEventId }).IsUnique();
            entity.HasIndex(row => row.CompletedAt);
            entity.HasOne(row => row.Subscription).WithMany().HasForeignKey(row => row.NotificationSubscriptionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<NotificationApproval>(entity =>
        {
            entity.Property(row => row.ExpiresAt).HasConversion(value => value.ToUnixTimeMilliseconds(), value => DateTimeOffset.FromUnixTimeMilliseconds(value));
            entity.HasIndex(row => new { row.ParentAccountId, row.MediaRequestId }).IsUnique();
            entity.HasOne<ParentAccount>().WithMany().HasForeignKey(row => row.ParentAccountId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<MediaRequest>().WithMany().HasForeignKey(row => row.MediaRequestId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Setting>(entity =>
        {
            entity.HasKey(setting => setting.Key);
            entity.Property(setting => setting.Key).HasMaxLength(200);
        });

        modelBuilder.Entity<ParentAccount>(entity =>
        {
            entity.Property(account => account.Username).HasMaxLength(100).UseCollation("NOCASE");
            entity.HasIndex(account => account.Username).IsUnique();
            entity.HasMany(account => account.Sessions)
                .WithOne(session => session.ParentAccount)
                .HasForeignKey(session => session.ParentAccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ParentSession>(entity =>
        {
            entity.Property(session => session.Id).HasMaxLength(64);
            entity.Property(session => session.UserAgent).HasMaxLength(256);
            entity.Property(session => session.RemoteAddress).HasMaxLength(64);
        });

        modelBuilder.Entity<KidProfile>(entity =>
        {
            entity.Property(profile => profile.Name).HasMaxLength(50).UseCollation("NOCASE");
            entity.Property(profile => profile.Avatar).HasMaxLength(32);
            entity.Property(profile => profile.PinHash).HasMaxLength(256);
            entity.Property(profile => profile.PlexLabel).HasMaxLength(64).UseCollation("NOCASE");
            entity.Property(profile => profile.PlexUserId).HasMaxLength(100);
            entity.Property(profile => profile.AutoApproveMovieRating).HasConversion<string>().HasMaxLength(32);
            entity.Property(profile => profile.AutoApproveTvRating).HasConversion<string>().HasMaxLength(32);
            entity.HasIndex(profile => profile.Name).IsUnique().HasFilter("\"ArchivedAt\" IS NULL");
            entity.HasIndex(profile => profile.PlexLabel).IsUnique().HasFilter("\"ArchivedAt\" IS NULL");
            entity.HasMany(profile => profile.Sessions)
                .WithOne(session => session.KidProfile)
                .HasForeignKey(session => session.KidProfileId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(profile => profile.PinAttempts)
                .WithOne(attempt => attempt.KidProfile)
                .HasForeignKey(attempt => attempt.KidProfileId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProfileTitleRule>(entity =>
        {
            entity.Property(row => row.Kind).HasConversion<string>();
            entity.HasKey(row => new { row.KidProfileId, row.Kind, row.TmdbId, row.Blocked });
            entity.Property(row => row.Title).HasMaxLength(300).UseCollation("NOCASE");
            entity.HasOne<KidProfile>().WithMany(profile => profile.TitleRules).HasForeignKey(row => row.KidProfileId).OnDelete(DeleteBehavior.Cascade);
        });


        modelBuilder.Entity<KidSession>(entity =>
        {
            entity.Property(session => session.Id).HasMaxLength(64);
            entity.Property(session => session.UserAgent).HasMaxLength(256);
            entity.Property(session => session.RemoteAddress).HasMaxLength(64);
        });

        modelBuilder.Entity<KidPinAttempt>(entity =>
        {
            entity.Property(attempt => attempt.DeviceHash).HasMaxLength(64);
            entity.HasIndex(attempt => new { attempt.KidProfileId, attempt.DeviceHash }).IsUnique();
            entity.HasIndex(attempt => new { attempt.KidProfileId, attempt.KidDeviceId }).IsUnique();
        });

        modelBuilder.Entity<KidDevice>(entity =>
        {
            entity.Property(device => device.Name).HasMaxLength(50);
            entity.Property(device => device.TokenHash).HasMaxLength(64);
            entity.HasMany(device => device.Profiles)
                .WithOne(assignment => assignment.KidDevice)
                .HasForeignKey(assignment => assignment.KidDeviceId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany<KidSession>()
                .WithOne(session => session.KidDevice)
                .HasForeignKey(session => session.KidDeviceId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany<KidPinAttempt>()
                .WithOne(attempt => attempt.KidDevice)
                .HasForeignKey(attempt => attempt.KidDeviceId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany<KidPairingCode>()
                .WithOne(code => code.KidDevice)
                .HasForeignKey(code => code.KidDeviceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<KidDeviceProfile>(entity =>
        {
            entity.HasKey(assignment => new { assignment.KidDeviceId, assignment.KidProfileId });
            entity.HasOne(assignment => assignment.KidProfile)
                .WithMany()
                .HasForeignKey(assignment => assignment.KidProfileId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<KidPairingCode>(entity =>
        {
            entity.Property(code => code.CodeHash).HasMaxLength(64);
            entity.HasIndex(code => code.KidDeviceId).IsUnique();
        });

        modelBuilder.Entity<OwnedLabel>(entity =>
        {
            entity.Property(label => label.ServerId).HasMaxLength(100);
            entity.Property(label => label.Guid).HasMaxLength(256);
            entity.Property(label => label.RatingKey).HasMaxLength(20);
            entity.Property(label => label.Label).HasMaxLength(64).UseCollation(LabelCollation.Name);
            entity.Property(label => label.Source).HasMaxLength(32);
            entity.HasIndex(label => new { label.ServerId, label.Guid, label.Label }).IsUnique();
            entity.HasOne(label => label.KidProfile)
                .WithMany()
                .HasForeignKey(label => label.KidProfileId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MediaRequest>(entity =>
        {
            entity.Property(request => request.Kind).HasConversion<string>().HasMaxLength(16);
            entity.Property(request => request.State).HasConversion<string>().HasMaxLength(16);
            entity.Property(request => request.Title).HasMaxLength(300);
            entity.Property(request => request.Backend).HasConversion<string>().HasMaxLength(16);
            entity.Property(request => request.Version).IsConcurrencyToken();
            entity.HasIndex(request => new { request.KidProfileId, request.Kind, request.TmdbId })
                .IsUnique()
                .HasFilter("\"State\" <> 'Denied' AND \"State\" <> 'Revoked'");
            entity.HasIndex(request => request.State);
            entity.HasOne(request => request.KidProfile)
                .WithMany()
                .HasForeignKey(request => request.KidProfileId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(request => request.Events)
                .WithOne(item => item.MediaRequest)
                .HasForeignKey(item => item.MediaRequestId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MediaRequestEvent>(entity =>
        {
            entity.Property(item => item.FromState).HasConversion<string>().HasMaxLength(16);
            entity.Property(item => item.ToState).HasConversion<string>().HasMaxLength(16);
            entity.Property(item => item.ActorKind).HasConversion<string>().HasMaxLength(16);
            entity.Property(item => item.ActorName).HasMaxLength(100);
            entity.Property(item => item.Reason).HasMaxLength(500);
        });

        modelBuilder.Entity<JobRun>(entity =>
        {
            entity.Property(run => run.JobName).HasMaxLength(100);
            entity.Property(run => run.Trigger).HasConversion<string>().HasMaxLength(16);
            entity.Property(run => run.Outcome).HasConversion<string>().HasMaxLength(16);
            entity.Property(run => run.Error).HasMaxLength(JobRun.MaxErrorLength);
            entity.HasIndex(run => new { run.JobName, run.Id });
        });

        modelBuilder.Entity<WebhookEvent>(entity =>
        {
            entity.Property(item => item.Source).HasMaxLength(WebhookEvent.MaxSourceLength);
            entity.Property(item => item.DeliveryKey).HasMaxLength(WebhookEvent.KeyLength);
            entity.Property(item => item.EventType).HasMaxLength(WebhookEvent.MaxEventTypeLength);
            entity.HasIndex(item => new { item.Source, item.DeliveryKey }).IsUnique();
            entity.HasIndex(item => item.ProcessedAt);
        });

        modelBuilder.Entity<LibraryItem>(entity =>
        {
            entity.Property(item => item.ServerId).HasMaxLength(100);
            entity.Property(item => item.RatingKey).HasMaxLength(20);
            entity.Property(item => item.Guid).HasMaxLength(256);
            entity.Property(item => item.Kind).HasConversion<string>().HasMaxLength(16);
            entity.Property(item => item.LibraryId).HasMaxLength(20);
            entity.Property(item => item.Title).HasMaxLength(500);
            entity.HasIndex(item => new { item.ServerId, item.RatingKey }).IsUnique();
            entity.HasIndex(item => item.TitleId);
            entity.HasOne(item => item.CanonicalTitle)
                .WithMany(title => title.Items)
                .HasForeignKey(item => item.TitleId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasMany(item => item.ExternalIds)
                .WithOne(id => id.Item)
                .HasForeignKey(id => id.ItemId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(item => item.Labels)
                .WithOne(label => label.Item)
                .HasForeignKey(label => label.ItemId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LibraryTitle>(entity =>
        {
            entity.Property(title => title.Kind).HasConversion<string>().HasMaxLength(16);
            entity.HasIndex(title => new { title.Kind, title.TmdbId }).IsUnique();
        });

        modelBuilder.Entity<LibraryExternalId>(entity =>
        {
            entity.Property(id => id.Source).HasConversion<string>().HasMaxLength(16);
            entity.Property(id => id.Value).HasMaxLength(256);
            entity.Property(id => id.Origin).HasConversion<string>().HasMaxLength(16);
            entity.HasIndex(id => new { id.Source, id.Value });
            entity.HasIndex(id => new { id.ItemId, id.Source, id.Value }).IsUnique();
        });

        modelBuilder.Entity<LibraryItemLabel>(entity =>
        {
            entity.Property(label => label.Label).HasMaxLength(LibraryItemLabel.MaxLength).UseCollation("NOCASE");
            entity.HasIndex(label => label.Label);
            entity.HasIndex(label => new { label.ItemId, label.Label }).IsUnique();
        });

        modelBuilder.Entity<LibraryRefreshState>(entity =>
        {
            entity.Property(state => state.Id).ValueGeneratedNever();
            entity.Property(state => state.ServerId).HasMaxLength(100);
        });

        modelBuilder.Entity<LabelGuardianReport>(entity =>
        {
            entity.Property(report => report.ServerId).HasMaxLength(100);
            entity.HasIndex(report => report.StartedAt);
            entity.HasMany(report => report.Entries)
                .WithOne(entry => entry.Report)
                .HasForeignKey(entry => entry.ReportId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LabelGuardianEntry>(entity =>
        {
            entity.Property(entry => entry.Kind).HasConversion<string>().HasMaxLength(16);
            entity.Property(entry => entry.Action).HasConversion<string>().HasMaxLength(16);
            entity.Property(entry => entry.Label).HasMaxLength(64);
            entity.Property(entry => entry.Guid).HasMaxLength(256);
            entity.Property(entry => entry.RatingKey).HasMaxLength(20);
            entity.Property(entry => entry.PreviousRatingKey).HasMaxLength(20);
            entity.Property(entry => entry.PreviousGuid).HasMaxLength(256);
            entity.Property(entry => entry.Title).HasMaxLength(LabelGuardianEntry.MaxTitleLength);
            entity.Property(entry => entry.Detail).HasMaxLength(LabelGuardianEntry.MaxDetailLength);
            entity.HasIndex(entry => new { entry.ReportId, entry.Id });
            entity.HasOne<KidProfile>()
                .WithMany()
                .HasForeignKey(entry => entry.KidProfileId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
