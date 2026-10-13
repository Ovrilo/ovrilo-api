using Microsoft.EntityFrameworkCore;

using Ovrilo.Modules.Identity.Domain;

namespace Ovrilo.Modules.Identity.Persistence;

internal sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options)
{
    public const string Schema = "identity";

    public DbSet<User> Users => Set<User>();

    public DbSet<UserSession> UserSessions => Set<UserSession>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<User>(user =>
        {
            user.HasKey(u => u.Id);
            user.Property(u => u.Username).HasMaxLength(User.UsernameMaxLength);
            user.Property(u => u.DisplayName).HasMaxLength(User.DisplayNameMaxLength);
            user.Property(u => u.Email).HasMaxLength(User.EmailMaxLength);
            user.Property(u => u.Status).HasConversion<string>().HasMaxLength(32);
            user.HasIndex(u => u.Username).IsUnique();
            user.HasIndex(u => u.Email).IsUnique();
        });

        modelBuilder.Entity<UserSession>(session =>
        {
            session.HasKey(s => s.Id);
            session.Property(s => s.DeviceLabel).HasMaxLength(UserSession.DeviceLabelMaxLength);
            session.Property(s => s.RefreshTokenHash).HasMaxLength(64);
            session.Property(s => s.RevocationReason).HasMaxLength(60);
            session.Property(s => s.Version).IsRowVersion();
            session.HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
            session.HasIndex(s => new { s.UserId, s.RevokedAt });
            session.HasIndex(s => s.ExpiresAt);
        });
    }
}
