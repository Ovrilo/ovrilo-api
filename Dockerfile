# Ovrilo API container image (ADR-0013).
# Build:   docker build -t ovrilo-api .
# Run:     configuration comes from environment variables (see README "Deployment"); no secrets in the image.
# Migrate: docker run --rm <env> ovrilo-api migrate   (explicit, authorized operation; exits when done)

FROM mcr.microsoft.com/dotnet/sdk:10.0.400 AS build
WORKDIR /src

# Restore first so the layer is cached until a project file changes.
COPY global.json Directory.Build.props .editorconfig dotnet-tools.json Ovrilo.slnx ./
COPY src/BuildingBlocks/Ovrilo.BuildingBlocks/Ovrilo.BuildingBlocks.csproj src/BuildingBlocks/Ovrilo.BuildingBlocks/
COPY src/Modules/Identity/Ovrilo.Modules.Identity.Contracts/Ovrilo.Modules.Identity.Contracts.csproj src/Modules/Identity/Ovrilo.Modules.Identity.Contracts/
COPY src/Modules/Identity/Ovrilo.Modules.Identity/Ovrilo.Modules.Identity.csproj src/Modules/Identity/Ovrilo.Modules.Identity/
COPY src/Modules/Communities/Ovrilo.Modules.Communities.Contracts/Ovrilo.Modules.Communities.Contracts.csproj src/Modules/Communities/Ovrilo.Modules.Communities.Contracts/
COPY src/Modules/Communities/Ovrilo.Modules.Communities/Ovrilo.Modules.Communities.csproj src/Modules/Communities/Ovrilo.Modules.Communities/
COPY src/Modules/Channels/Ovrilo.Modules.Channels.Contracts/Ovrilo.Modules.Channels.Contracts.csproj src/Modules/Channels/Ovrilo.Modules.Channels.Contracts/
COPY src/Modules/Channels/Ovrilo.Modules.Channels/Ovrilo.Modules.Channels.csproj src/Modules/Channels/Ovrilo.Modules.Channels/
COPY src/Modules/VoiceSessions/Ovrilo.Modules.VoiceSessions/Ovrilo.Modules.VoiceSessions.csproj src/Modules/VoiceSessions/Ovrilo.Modules.VoiceSessions/
COPY src/Ovrilo.Api/Ovrilo.Api.csproj src/Ovrilo.Api/
RUN dotnet restore src/Ovrilo.Api/Ovrilo.Api.csproj

COPY src/ src/
# The committed OpenAPI document is the contract; do not regenerate it inside the image build.
RUN dotnet publish src/Ovrilo.Api/Ovrilo.Api.csproj -c Release -o /app --no-restore \
    -p:OpenApiGenerateDocuments=false -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.11 AS runtime
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=0
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Ovrilo.Api.dll"]
