using Microsoft.EntityFrameworkCore;

namespace Wishlistarr.Data;

public sealed class WishlistarrDbContext(DbContextOptions<WishlistarrDbContext> options) : DbContext(options)
{
    public DbSet<Setting> Settings => this.Set<Setting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<Setting>(entity =>
        {
            entity.HasKey(setting => setting.Key);
            entity.Property(setting => setting.Key).HasMaxLength(200);
        });
}
