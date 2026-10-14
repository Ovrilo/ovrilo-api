using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

using Ovrilo.BuildingBlocks.Persistence;
using Ovrilo.Modules.Media.Domain;

namespace Ovrilo.Modules.Media.Persistence;

internal sealed class MediaDbContext(DbContextOptions<MediaDbContext> options) : DbContext(options)
{
    public const string Schema = "media";

    public DbSet<MediaAsset> Assets => Set<MediaAsset>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<MediaAsset>(asset =>
        {
            asset.ToTable("media_assets");
            asset.HasKey(a => a.Id);
            asset.Property(a => a.Id).ValueGeneratedNever();
            asset.Property(a => a.Bucket).HasMaxLength(128);
            asset.Property(a => a.ObjectKey).HasMaxLength(1024);
            asset.Property(a => a.OriginalFilename).HasMaxLength(MediaAsset.OriginalFilenameMaxLength);
            asset.Property(a => a.DeclaredContentType).HasMaxLength(255);
            asset.Property(a => a.DetectedContentType).HasMaxLength(255);
            asset.HasIndex(a => new { a.Bucket, a.ObjectKey }).IsUnique();
            asset.HasIndex(a => new { a.OwnerId, a.CreatedAt }).IsDescending(false, true);
            // The janitor scans by status and age.
            asset.HasIndex(a => new { a.Status, a.CreatedAt });
        });
    }
}

/// <summary>Used only by <c>dotnet ef</c> to create migrations; never connects at design time.</summary>
internal sealed class DesignTimeMediaDbContextFactory : IDesignTimeDbContextFactory<MediaDbContext>
{
    public MediaDbContext CreateDbContext(string[] args) =>
        new(ModuleDatabase.DesignTimeOptions<MediaDbContext>(MediaDbContext.Schema));
}
