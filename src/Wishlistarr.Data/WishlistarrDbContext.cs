using Microsoft.EntityFrameworkCore;

namespace Wishlistarr.Data;

public sealed class WishlistarrDbContext(DbContextOptions<WishlistarrDbContext> options) : DbContext(options)
{
    public DbSet<Setting> Settings => this.Set<Setting>();

    public DbSet<ParentAccount> ParentAccounts => this.Set<ParentAccount>();

    public DbSet<ParentSession> ParentSessions => this.Set<ParentSession>();

    public DbSet<KidProfile> KidProfiles => this.Set<KidProfile>();

    public DbSet<KidSession> KidSessions => this.Set<KidSession>();

    public DbSet<KidPinAttempt> KidPinAttempts => this.Set<KidPinAttempt>();

    public DbSet<OwnedLabel> OwnedLabels => this.Set<OwnedLabel>();

    public DbSet<MediaRequest> MediaRequests => this.Set<MediaRequest>();

    public DbSet<MediaRequestEvent> MediaRequestEvents => this.Set<MediaRequestEvent>();

    public DbSet<JobRun> JobRuns => this.Set<JobRun>();

    public DbSet<WebhookEvent> WebhookEvents => this.Set<WebhookEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
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
        });

        modelBuilder.Entity<OwnedLabel>(entity =>
        {
            entity.Property(label => label.ServerId).HasMaxLength(100);
            entity.Property(label => label.Guid).HasMaxLength(256);
            entity.Property(label => label.RatingKey).HasMaxLength(20);
            entity.Property(label => label.Label).HasMaxLength(64).UseCollation("NOCASE");
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
            entity.Property(request => request.Version).IsConcurrencyToken();
            entity.HasIndex(request => new { request.KidProfileId, request.Kind, request.TmdbId })
                .IsUnique()
                .HasFilter("\"State\" <> 'Denied'");
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
    }
}
