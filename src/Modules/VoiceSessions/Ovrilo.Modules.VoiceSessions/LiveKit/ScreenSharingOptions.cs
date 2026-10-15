namespace Ovrilo.Modules.VoiceSessions.LiveKit;

/// <summary>
/// Runtime flag for screen sharing (ADR-0017, Proposed). Off by default: join grants allow only the
/// microphone and the watch endpoint refuses. Turn on (<c>VoiceSessions:ScreenSharing:Enabled</c>) only with a desktop
/// build that implements sharing; remove the flag once the feature is complete and accepted.
/// </summary>
internal sealed class ScreenSharingOptions
{
    public const string SectionName = "VoiceSessions:ScreenSharing";

    public bool Enabled { get; set; }
}
