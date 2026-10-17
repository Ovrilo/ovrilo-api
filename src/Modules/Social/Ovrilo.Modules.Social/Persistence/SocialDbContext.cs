using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

using Ovrilo.BuildingBlocks.Persistence;
using Ovrilo.Modules.Social.Domain;

namespace Ovrilo.Modules.Social.Persistence;

internal sealed class SocialDbContext(DbContextOptions<SocialDbContext> options) : DbContext(options)
{
    public const string Schema = "social";
    public const string UniquePendingRequestIndex = "ix_friend_requests_pending_pair";

    public DbSet<FriendRequest> FriendRequests => Set<FriendRequest>();

    public DbSet<Friendship> Friendships => Set<Friendship>();

    public DbSet<UserBlock> Blocks => Set<UserBlock>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<FriendRequest>(request =>
        {
            request.HasKey(r => r.Id);
            request.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
            // At most one pending request per direction; resolved requests are history.
            request.HasIndex(r => new { r.RequesterId, r.RecipientId })
                .IsUnique()
                .HasFilter("status = 'Pending'")
                .HasDatabaseName(UniquePendingRequestIndex);
            request.HasIndex(r => new { r.RecipientId, r.Status });
        });

        modelBuilder.Entity<Friendship>(friendship =>
        {
            friendship.HasKey(f => new { f.UserLowId, f.UserHighId });
            friendship.HasIndex(f => f.UserHighId);
        });

        modelBuilder.Entity<UserBlock>(block =>
        {
            block.ToTable("blocks");
            block.HasKey(b => new { b.BlockerId, b.BlockedId });
            block.HasIndex(b => b.BlockedId);
        });
    }
}

/// <summary>Used only by <c>dotnet ef</c> to create migrations; never connects at design time.</summary>
internal sealed class DesignTimeSocialDbContextFactory : IDesignTimeDbContextFactory<SocialDbContext>
{
    public SocialDbContext CreateDbContext(string[] args) =>
        new(ModuleDatabase.DesignTimeOptions<SocialDbContext>(SocialDbContext.Schema));
}
