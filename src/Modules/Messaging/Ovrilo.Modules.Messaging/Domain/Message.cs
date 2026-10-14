namespace Ovrilo.Modules.Messaging.Domain;

/// <summary>A text-channel message (docs/domain/messaging.md). Deletion keeps a tombstone row.</summary>
internal sealed class Message
{
    public const int ContentMaxLength = 4000;

    /// <summary>Server-assigned, increasing per database; the pagination cursor.</summary>
    public long Id { get; init; }

    public Guid ChannelId { get; init; }

    public Guid AuthorId { get; init; }

    /// <summary>Client-generated key; unique per author so retried sends return the same message.</summary>
    public Guid ClientMessageId { get; init; }

    /// <summary>Empty once deleted.</summary>
    public required string Content { get; set; }

    public long? ReplyToMessageId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? EditedAt { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>PostgreSQL <c>xmin</c> optimistic concurrency token.</summary>
    public uint Version { get; set; }
}
