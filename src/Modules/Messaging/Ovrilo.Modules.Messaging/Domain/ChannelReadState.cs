namespace Ovrilo.Modules.Messaging.Domain;

/// <summary>
/// How far a user has read a text channel (docs/domain/messaging.md). Only moves forward, so
/// devices that report an older position never mark messages unread again.
/// </summary>
internal sealed class ChannelReadState
{
    public Guid UserId { get; init; }

    public Guid ChannelId { get; init; }

    /// <summary>Newest message ID the user has seen in the channel.</summary>
    public long LastReadMessageId { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
