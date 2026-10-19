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
    }
}
