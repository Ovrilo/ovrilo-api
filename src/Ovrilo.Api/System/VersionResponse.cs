namespace Ovrilo.Api.System;

/// <summary>Build metadata used by clients and operators to check API compatibility.</summary>
/// <param name="Version">API build version.</param>
/// <param name="ContractVersion">HTTP contract version (matches the OpenAPI document version).</param>
public sealed record VersionResponse(string Version, string ContractVersion);
