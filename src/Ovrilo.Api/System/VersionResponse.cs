namespace Ovrilo.Api.System;

/// <summary>Build metadata used by clients and operators to check API compatibility.</summary>
public sealed record VersionResponse(string Version);
