using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Wishlistarr.Data;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<WishlistarrDbContext>
{
    public WishlistarrDbContext CreateDbContext(string[] args)
    {
        DbContextOptionsBuilder<WishlistarrDbContext> builder = new();
        builder.UseSqlite("Data Source=design-time.db");
        return new WishlistarrDbContext(builder.Options);
    }
}
