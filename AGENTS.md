# ovrilo-api — Agent Instructions

Read the workspace `../AGENTS.md` first. **Never commit, stage, push, deploy or run production migrations without explicit user authorization.**

- Stack: ASP.NET Core (.NET 10), PostgreSQL via EF Core/Npgsql, SignalR. Modular monolith with vertical slices (ADR-0004).
- Each module owns its schema, migrations and contracts; no cross-module DbContext/entity access. Add architecture tests when adding modules.
- Read the module's `docs/domain/*.md` spec before implementing it; specs are proposals, ADRs are authoritative.
- Update OpenAPI-visible contracts deliberately; prefer additive changes (see `docs/development/VERSIONING.md`).
- Integration tests use disposable containers/databases with non-production credentials.
- Validate: `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes`.
