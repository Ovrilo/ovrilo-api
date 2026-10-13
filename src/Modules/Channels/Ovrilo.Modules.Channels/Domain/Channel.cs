using Ovrilo.Modules.Channels.Contracts;

namespace Ovrilo.Modules.Channels.Domain;

internal sealed class Channel
{
    public const int NameMaxLength = 100;
    public const int TopicMaxLength = 1024;

    public Guid Id { get; init; }

    public Guid CommunityId { get; init; }

    public required string Name { get; set; }

    /// <summary>Lowercase name used for case-insensitive uniqueness per community and type.</summary>
    public required string NormalizedName { get; set; }

    public ChannelType Type { get; init; }

    public string? Topic { get; set; }

    public int Position { get; set; }

    /// <summary>Reserved for channel permission overrides (deferred); always false in Phase 01.</summary>
    public bool IsPrivate { get; init; }

    public Guid CreatedBy { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public void Rename(string name)
    {
        Name = name;
        NormalizedName = name.ToLowerInvariant();
    }
}
