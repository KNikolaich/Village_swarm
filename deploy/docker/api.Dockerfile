# syntax=docker/dockerfile:1
# Hive api (spec 13.3). Build from the repo root:
#   docker buildx build --platform linux/arm64 -f deploy/docker/api.Dockerfile -t village-swarm-api .
# The SDK stage runs natively and cross-publishes for the target, so no emulation is needed for dotnet.

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
# Not VERSION: MSBuild would take that environment variable as the package version.
ARG HIVE_VERSION=0.0.0
WORKDIR /src
COPY backend/global.json backend/Directory.Build.props backend/Directory.Packages.props backend/
COPY backend/src/ backend/src/
# The OpenAPI document is written by developer builds for the frontend; the image does not need it.
# Docker says amd64 where .NET says x64.
RUN arch="$([ "$TARGETARCH" = amd64 ] && echo x64 || echo "$TARGETARCH")" \
 && dotnet publish backend/src/Hive.Api -c Release -a "$arch" --self-contained false -o /out \
      -p:OpenApiGenerateDocuments=false -p:InformationalVersion="$HIVE_VERSION"

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out ./
# Flashable hornet images for the "add hornet" wizard, when firmware/dist was built before the image.
COPY firmware/dis[t] /app/firmware/
# RPi with 2 GB: workstation GC that gives memory back eagerly (spec 13.2 memory budget).
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    Provisioning__FirmwareDir=/app/firmware \
    Media__Root=/srv/hive/media \
    DOTNET_gcServer=0 \
    DOTNET_GCConserveMemory=7 \
    DOTNET_TieredPGO=0
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "Hive.Api.dll"]
