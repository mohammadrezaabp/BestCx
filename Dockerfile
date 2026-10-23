# ---------------------------------------------------------------------------
# BestCx - ONE image containing the whole platform.
#
# Stage 1 builds the React console, stage 2 builds the .NET solution, and the
# final image runs a single process that hosts every API and worker. Which
# roles actually start is decided at runtime by CX_ROLES, so the same image can
# later be deployed as several narrower containers without rebuilding.
# ---------------------------------------------------------------------------

# Build with the original public upstream images.

# --------------------------------------------------------------- web console
FROM node:22-alpine AS web
WORKDIR /web

COPY src/Cx.Web/package.json src/Cx.Web/package-lock.json ./
RUN npm ci --no-audit --no-fund

COPY src/Cx.Web/ ./
RUN npm run build

# ----------------------------------------------------------------- .NET build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first, against project files only, so the layer caches across code edits.
COPY global.json Directory.Build.props Directory.Packages.props BestCx.sln ./
COPY src/Cx.Contracts/Cx.Contracts.csproj                     src/Cx.Contracts/
COPY src/Cx.Domain/Cx.Domain.csproj                           src/Cx.Domain/
COPY src/Cx.Application/Cx.Application.csproj                 src/Cx.Application/
COPY src/Cx.Infrastructure/Cx.Infrastructure.csproj           src/Cx.Infrastructure/
COPY src/Cx.Ingestion.Api/Cx.Ingestion.Api.csproj             src/Cx.Ingestion.Api/
COPY src/Cx.Query.Api/Cx.Query.Api.csproj                     src/Cx.Query.Api/
COPY src/Cx.Admin.Api/Cx.Admin.Api.csproj                     src/Cx.Admin.Api/
COPY src/Cx.Persistence.Worker/Cx.Persistence.Worker.csproj   src/Cx.Persistence.Worker/
COPY src/Cx.Ai.Orchestrator/Cx.Ai.Orchestrator.csproj         src/Cx.Ai.Orchestrator/
COPY src/Cx.Ai.Worker/Cx.Ai.Worker.csproj                     src/Cx.Ai.Worker/
COPY src/Cx.Voice.Worker/Cx.Voice.Worker.csproj               src/Cx.Voice.Worker/
COPY src/Cx.Integrations.Support/Cx.Integrations.Support.csproj src/Cx.Integrations.Support/
COPY src/Cx.Integrations.Voip/Cx.Integrations.Voip.csproj     src/Cx.Integrations.Voip/
COPY src/Cx.Host/Cx.Host.csproj                               src/Cx.Host/

RUN dotnet restore src/Cx.Host/Cx.Host.csproj

COPY src/ src/
RUN dotnet publish src/Cx.Host/Cx.Host.csproj \
      -c Release \
      -o /app/publish \
      --no-restore \
      /p:UseAppHost=false

# ----------------------------------------------------------------- runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# curl is used by the container healthcheck below.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./
COPY --from=web /web/dist ./wwwroot
# Schema migrations and the opt-in demo fixture are applied by the app on startup.
COPY deploy/postgres ./deploy/postgres

# Run as a non-root user.
RUN useradd --uid 10001 --create-home --shell /usr/sbin/nologin cx \
    && chown -R cx:cx /app
USER cx

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=1 \
    CX_ROLES=all

EXPOSE 8080

HEALTHCHECK --interval=20s --timeout=5s --start-period=60s --retries=5 \
  CMD curl -fsS http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "Cx.Host.dll"]
