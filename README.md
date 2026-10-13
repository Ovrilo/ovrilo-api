# ovrilo-api

ASP.NET Core modular monolith for [Ovrilo](https://github.com/Ovrilo): identity, communities, channels, messaging, presence, voice-session authorization and media metadata. Deployed independently to Coolify.

## Prerequisites

- .NET SDK 10
- Docker; local PostgreSQL/LiveKit/RustFS run from `../ovrilo-infra/local` (`docker compose up -d --wait`)

## Commands

```bash
dotnet build
dotnet test
dotnet format --verify-no-changes
dotnet run --project src/Ovrilo.Api      # http://localhost:5158
```

Endpoints: `GET /health`, `GET /version`; `GET /openapi/v1.json` in Development.

## OpenAPI contract

`dotnet build` regenerates `openapi/ovrilo-api.json`. Commit it with the code change; CI fails when it is stale. The contract version is `ApiContract.Version`; errors use RFC 9457 Problem Details. See `docs/development/CONTRACTS.md` in the workspace.

## Layout

- `src/Ovrilo.Api` — host: composition root, middleware, endpoint mapping.
- `tests/Ovrilo.Api.Tests` — integration tests via `WebApplicationFactory`.
- Modules (`Identity`, `Communities`, `Channels`, `Messaging`, `Social`, `Presence`, `VoiceSessions`, `Media`) are added per phase as vertical slices with their own PostgreSQL schema.

## Architecture rules

- Modules never access another module's DbContext, entities or tables; integrate via contracts/events (ADR-0004).
- Persist before broadcasting; SignalR is never the source of truth (ADR-0007).
- The API never proxies voice media; it issues short-lived LiveKit grants after authorization (ADR-0006).
- Binary media lives in S3-compatible storage behind `IObjectStorage` (ADR-0014).
- OpenAPI is the client contract; the desktop generates its TypeScript client from it.
