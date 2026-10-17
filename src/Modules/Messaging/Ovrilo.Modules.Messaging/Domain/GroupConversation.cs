namespace Ovrilo.Modules.Messaging.Domain;

/// <summary>
/// A conversation of up to <see cref="MaxMembers"/> people (docs/domain/messaging.md). Like a direct
/// conversation, its ID is used where a channel ID is used. Members need not be friends with each
/// other; whoever adds someone must be their friend.
/// </summary>
internal sealed class GroupConversation
{
    public const int MaxMembers = 10;
    public const int NameMaxLength = 100;

    public Guid Id { get; init; }

    /// <summary>Optional; clients show the members' names when it is empty.</summary>
    public string? Name { get; set; }

    /// <summary>May remove members. Passes to the longest-standing member when the owner leaves.</summary>
    public Guid OwnerId { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public List<GroupMember> Members { get; init; } = [];

    public bool Includes(Guid userId) => Members.Exists(m => m.UserId == userId);
}

internal sealed class GroupMember
{
    public Guid ConversationId { get; init; }

    public Guid UserId { get; init; }

    public DateTimeOffset JoinedAt { get; init; }
}
