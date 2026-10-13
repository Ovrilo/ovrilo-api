using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

using Ovrilo.BuildingBlocks.Persistence;
using Ovrilo.Modules.Channels.Domain;

namespace Ovrilo.Modules.Channels.Persistence;

internal sealed class ChannelsDbContext(DbContextOptions<ChannelsDbContext> options) : DbContext(options)
{
    public const string Schema = "channels";
    public const string UniqueActiveNameIndex = "ix_channels_community_id_type_normalized_name";

    public DbSet<Channel> Channels => Set<Channel>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Channel>(channel =>
        {
            channel.HasKey(c => c.Id);
            channel.Property(c => c.Name).HasMaxLength(Channel.NameMaxLength);
            channel.Property(c => c.NormalizedName).HasMaxLength(Channel.NameMaxLength);
            channel.Property(c => c.Topic).HasMaxLength(Channel.TopicMaxLength);
            channel.Property(c => c.Type).HasConversion<string>().HasMaxLength(16);
            channel.HasIndex(c => new { c.CommunityId, c.Type, c.NormalizedName })
                .IsUnique()
                .HasFilter("deleted_at IS NULL")
                .HasDatabaseName(UniqueActiveNameIndex);
            channel.HasIndex(c => new { c.CommunityId, c.Position });
        });
    }
}

/// <summary>Used only by <c>dotnet ef</c> to create migrations; never connects at design time.</summary>
internal sealed class DesignTimeChannelsDbContextFactory : IDesignTimeDbContextFactory<ChannelsDbContext>
{
    public ChannelsDbContext CreateDbContext(string[] args) =>
        new(ModuleDatabase.DesignTimeOptions<ChannelsDbContext>(ChannelsDbContext.Schema));
}
