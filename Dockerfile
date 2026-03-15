# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0-preview-alpine AS build
WORKDIR /src
COPY src/*.csproj .
RUN dotnet restore
COPY src/ .
RUN dotnet publish -c Release -o /app --no-restore

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview-alpine
RUN apk add --no-cache curl
RUN addgroup -g 100 -S users 2>/dev/null; adduser -u 1046 -G users -S app
WORKDIR /app
COPY --from=build --chown=app:users /app .
USER app
EXPOSE 16004
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:16004/health || exit 1
ENTRYPOINT ["dotnet", "MediaSkipDetector.dll"]
