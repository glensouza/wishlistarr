using Microsoft.EntityFrameworkCore;

namespace Wishlistarr.Data;

public sealed class WishlistarrDbContext(DbContextOptions<WishlistarrDbContext> options) : DbContext(options)
{
    public DbSet<Setting> Settings => this.Set<Setting>();

    public DbSet<ParentAccount> ParentAccounts => this.Set<ParentAccount>();

    public DbSet<ParentSession> ParentSessions => this.Set<ParentSession>();

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
    }
}
