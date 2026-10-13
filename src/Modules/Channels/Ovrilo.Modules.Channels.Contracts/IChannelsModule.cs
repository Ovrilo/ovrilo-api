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

    /// <summary>True only for voice channels the user may join.</summary>
    Task<bool> CanJoinVoiceAsync(Guid channelId, Guid userId, CancellationToken cancellationToken = default);
}
