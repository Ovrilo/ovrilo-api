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
dotnet run --project src/Ovrilo.Api      # http://localhost:5158 (Development: applies migrations on startup)
```

Start local infrastructure first (`../ovrilo-infra/local`, `docker compose up -d --wait`). Integration tests start their own disposable PostgreSQL container via Testcontainers and need Docker running.

### Migrations

Each module owns its schema and migrations. Example for Identity:

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project src/Modules/Identity/Ovrilo.Modules.Identity --startup-project src/Ovrilo.Api --output-dir Persistence/Migrations
```

`Database:ApplyMigrationsOnStartup` is enabled only in Development/tests. Production migrations are explicit, authorized operations.

Endpoints: `GET /health`, `GET /version`; `GET /openapi/v1.json` in Development.

## OpenAPI contract

`dotnet build` regenerates `openapi/ovrilo-api.json`. Commit it with the code change; CI fails when it is stale. The contract version is `ApiContract.Version`; errors use RFC 9457 Problem Details. See `docs/development/CONTRACTS.md` in the workspace.

## Layout

- `src/Ovrilo.Api` — host: composition root, middleware, endpoint mapping.
- `src/Modules/<Module>/Ovrilo.Modules.<Module>` — module implementation (internal domain, persistence, endpoints).
- `src/Modules/<Module>/Ovrilo.Modules.<Module>.Contracts` — the only assembly other modules may reference.
- `tests/Ovrilo.Api.Tests` — integration tests via `WebApplicationFactory` + Testcontainers PostgreSQL.
- `tests/Ovrilo.ArchitectureTests` — module boundary rules (ADR-0004).
- Implemented modules: `Identity` (accounts, password sign-in, rotating refresh sessions; ADR-0012). Others (`Communities`, `Channels`, `Messaging`, `Social`, `Presence`, `VoiceSessions`, `Media`) are added per phase.

## Configuration

| Key | Notes |
|---|---|
| `ConnectionStrings:Ovrilo` | PostgreSQL connection string |
| `Identity:Jwt:SigningKey` | ≥32 bytes; required outside Development (secret) |
| `Identity:Jwt:AccessTokenLifetimeMinutes` / `SessionLifetimeDays` | Defaults 15 / 30 |
| `Identity:RateLimit:PermitLimit` / `WindowSeconds` | Per-IP limit for register/login/refresh; defaults 10 / 60 |
| `Database:ApplyMigrationsOnStartup` | Development/tests only |

## Architecture rules

- Modules never access another module's DbContext, entities or tables; integrate via contracts/events (ADR-0004).
- Persist before broadcasting; SignalR is never the source of truth (ADR-0007).
- The API never proxies voice media; it issues short-lived LiveKit grants after authorization (ADR-0006).
- Binary media lives in S3-compatible storage behind `IObjectStorage` (ADR-0014).
- OpenAPI is the client contract; the desktop generates its TypeScript client from it.
