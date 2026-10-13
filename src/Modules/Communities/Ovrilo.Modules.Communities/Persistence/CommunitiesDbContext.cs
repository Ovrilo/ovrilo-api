using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

using Ovrilo.BuildingBlocks.Persistence;
using Ovrilo.Modules.Communities.Domain;

namespace Ovrilo.Modules.Communities.Persistence;

internal sealed class CommunitiesDbContext(DbContextOptions<CommunitiesDbContext> options) : DbContext(options)
{
    public const string Schema = "communities";

    public DbSet<Community> Communities => Set<Community>();

    public DbSet<CommunityMember> Members => Set<CommunityMember>();

    public DbSet<Invitation> Invitations => Set<Invitation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Community>(community =>
        {
            community.HasKey(c => c.Id);
            community.Property(c => c.Name).HasMaxLength(Community.NameMaxLength);
            community.Property(c => c.Description).HasMaxLength(Community.DescriptionMaxLength);
            community.Property(c => c.Visibility).HasConversion<string>().HasMaxLength(32);
            community.HasIndex(c => c.OwnerId);
        });

        modelBuilder.Entity<CommunityMember>(member =>
        {
            member.ToTable("community_members");
            member.HasKey(m => m.Id);
            member.Ignore(m => m.Permissions);
            member.Property(m => m.Role).HasConversion<string>().HasMaxLength(32);
            member.Property(m => m.State).HasConversion<string>().HasMaxLength(32);
            member.Property(m => m.Nickname).HasMaxLength(CommunityMember.NicknameMaxLength);
            member.HasOne<Community>().WithMany().HasForeignKey(m => m.CommunityId).OnDelete(DeleteBehavior.Cascade);
            member.HasIndex(m => new { m.CommunityId, m.UserId }).IsUnique();
            member.HasIndex(m => m.UserId);
        });

        modelBuilder.Entity<Invitation>(invitation =>
        {
            invitation.HasKey(i => i.Id);
            invitation.Property(i => i.CodeHash).HasMaxLength(64);
            invitation.HasOne<Community>().WithMany().HasForeignKey(i => i.CommunityId).OnDelete(DeleteBehavior.Cascade);
            invitation.HasIndex(i => i.CodeHash).IsUnique();
            invitation.HasIndex(i => i.CommunityId);
        });
    }
}

/// <summary>Used only by <c>dotnet ef</c> to create migrations; never connects at design time.</summary>
internal sealed class DesignTimeCommunitiesDbContextFactory : IDesignTimeDbContextFactory<CommunitiesDbContext>
{
    public CommunitiesDbContext CreateDbContext(string[] args) =>
        new(ModuleDatabase.DesignTimeOptions<CommunitiesDbContext>(CommunitiesDbContext.Schema));
}
