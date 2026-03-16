# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0-preview-alpine AS build
WORKDIR /src
COPY src/ .
# Vendor directory sits beside src/; .csproj references ../vendor/
COPY vendor/ /vendor/
RUN dotnet publish -c Release -o /app

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview-alpine
RUN apk add --no-cache curl chromaprint ffmpeg
# The ASP.NET Alpine image includes an 'app' user (UID 1654).
# In production, docker-compose user: directive overrides to match NAS permissions.
WORKDIR /app
COPY --from=build /app .
VOLUME /data
USER app
EXPOSE 16004
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:16004/health || exit 1
ENTRYPOINT ["dotnet", "MediaSkipDetector.dll"]
