namespace Ovrilo.Modules.VoiceSessions.LiveKit;

/// <summary>
/// Runtime switch for screen sharing (ADR-0017, Proposed). On by default since desktop 0.6.0 shares
/// and watches; older desktops ignore it. Set <c>VoiceSessions:ScreenSharing:Enabled</c> to <c>false</c> to turn
/// it off: join grants then allow only the microphone and the watch endpoint refuses. Remove the
/// switch once the ADR is accepted.
/// </summary>
internal sealed class ScreenSharingOptions
{
    public const string SectionName = "VoiceSessions:ScreenSharing";

    public bool Enabled { get; set; } = true;
}
