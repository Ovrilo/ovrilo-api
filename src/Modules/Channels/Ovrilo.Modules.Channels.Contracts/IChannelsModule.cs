namespace Ovrilo.Modules.Channels.Contracts;

public enum ChannelType
{
    Text = 0,
    Voice = 1,
}

/// <summary>In-process authorization contract used by Messaging (Phase 04) and Voice Sessions (Phase 02).</summary>
public interface IChannelsModule
{
    /// <summary>True when the channel exists, is not deleted and the user may view it.</summary>
    Task<bool> CanReadChannelAsync(Guid channelId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>True only for text channels the user may post in.</summary>
    Task<bool> CanWriteMessagesAsync(Guid channelId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>True only for text channels where the user may delete other people's messages.</summary>
    Task<bool> CanManageMessagesAsync(Guid channelId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>True only for voice channels the user may join.</summary>
    Task<bool> CanJoinVoiceAsync(Guid channelId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Text channels of a community the user may read, in display order; null when the community
    /// does not exist or the user may not view its channels.
    /// </summary>
    Task<IReadOnlyList<Guid>?> ListReadableTextChannelIdsAsync(Guid communityId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Non-deleted voice channels of a community, in display order. No permission check: callers authorize first.</summary>
    Task<IReadOnlyList<Guid>> ListVoiceChannelIdsAsync(Guid communityId, CancellationToken cancellationToken = default);
}
