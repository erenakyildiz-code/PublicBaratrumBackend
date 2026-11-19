using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Baratrum.Models; // Ensure this matches the namespace of your ApplicationUser class

namespace Baratrum.Data;

// Inherit from IdentityDbContext using your custom ApplicationUser model
public class AppDbContext : IdentityDbContext<ApplicationUser>
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    // Add your DbSets here
    public DbSet<Payment> Payments { get; set; }
    public DbSet<Balance> Balances { get; set; }
    public DbSet<ShortTermMemory> ShortTermMemories { get; set; }
    public DbSet<LongTermMemory> LongTermMemories { get; set; }
    public DbSet<L2> L2Cache {get; set;}
    public DbSet<L3> L3Cache {get; set;}
    public DbSet<AgenticContextRecord> AgenticContextRecords {get; set;}
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Payment>()
            .HasOne(p => p.User)
            .WithMany()
            .HasForeignKey(p => p.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<Balance>(entity =>
        {
            entity.HasOne(b => b.User)
                .WithMany()
                .HasForeignKey(b => b.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(b => b.CurrentBalance)
                .HasPrecision(18, 3);
        });

        builder.Entity<ShortTermMemory>()
            .HasIndex(m => new { m.UserId, m.MemoryGenerationDate })
            .HasDatabaseName("IX_Active_Context")
            .HasFilter("[IsConsumed] = 0");
    }
}