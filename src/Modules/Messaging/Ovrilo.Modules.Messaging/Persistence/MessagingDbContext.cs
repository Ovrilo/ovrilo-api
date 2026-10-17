using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

using Ovrilo.BuildingBlocks.Persistence;
using Ovrilo.Modules.Messaging.Domain;

namespace Ovrilo.Modules.Messaging.Persistence;

internal sealed class MessagingDbContext(DbContextOptions<MessagingDbContext> options) : DbContext(options)
{
    public const string Schema = "messaging";
    public const string UniqueClientMessageIndex = "ix_messages_author_id_client_message_id";

    public DbSet<Message> Messages => Set<Message>();

    public DbSet<ChannelReadState> ReadStates => Set<ChannelReadState>();

    public DbSet<DirectConversation> DirectConversations => Set<DirectConversation>();

    public DbSet<GroupConversation> GroupConversations => Set<GroupConversation>();

    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Message>(message =>
        {
            message.HasKey(m => m.Id);
            message.Property(m => m.Id).UseIdentityAlwaysColumn();
            message.Property(m => m.Content).HasMaxLength(Message.ContentMaxLength);
            message.Property(m => m.Version).IsRowVersion();
            // Idempotent sends: one author cannot reuse (or collide with) their own client key.
            message.HasIndex(m => new { m.AuthorId, m.ClientMessageId })
                .IsUnique()
                .HasDatabaseName(UniqueClientMessageIndex);
            // Keyset pagination within a channel.
            message.HasIndex(m => new { m.ChannelId, m.Id }).IsDescending(false, true);
        });

        modelBuilder.Entity<ChannelReadState>(state =>
        {
            state.ToTable("channel_read_states");
            state.HasKey(r => new { r.UserId, r.ChannelId });
        });

        modelBuilder.Entity<DirectConversation>(conversation =>
        {
            conversation.HasKey(c => c.Id);
            conversation.HasIndex(c => new { c.UserLowId, c.UserHighId }).IsUnique();
            conversation.HasIndex(c => c.UserHighId);
        });

        modelBuilder.Entity<GroupConversation>(group =>
        {
            group.HasKey(g => g.Id);
            group.Property(g => g.Name).HasMaxLength(GroupConversation.NameMaxLength);
            group.HasMany(g => g.Members).WithOne().HasForeignKey(m => m.ConversationId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GroupMember>(member =>
        {
            member.HasKey(m => new { m.ConversationId, m.UserId });
            // "My groups".
            member.HasIndex(m => m.UserId);
        });
    }
}

/// <summary>Used only by <c>dotnet ef</c> to create migrations; never connects at design time.</summary>
internal sealed class DesignTimeMessagingDbContextFactory : IDesignTimeDbContextFactory<MessagingDbContext>
{
    public MessagingDbContext CreateDbContext(string[] args) =>
        new(ModuleDatabase.DesignTimeOptions<MessagingDbContext>(MessagingDbContext.Schema));
}
