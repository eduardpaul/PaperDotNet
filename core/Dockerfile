# syntax=docker/dockerfile:1
# The AOT core (ADR-0039): one self-contained native binary on runtime-deps (no .NET runtime in the image).
# Build from the repository root: docker build -f core/Dockerfile -t paperdotnet-core core
FROM mcr.microsoft.com/dotnet/sdk:11.0-preview AS build
# Native AOT needs a C toolchain and zlib.
RUN apt-get update && apt-get install -y --no-install-recommends clang zlib1g-dev && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props PaperDotNet.Core.slnx ./
COPY .config/ .config/
COPY src/ src/
RUN dotnet publish src/PaperDotNet.Core.Host/PaperDotNet.Core.Host.csproj -c Release -r linux-x64 -o /app

FROM mcr.microsoft.com/dotnet/runtime-deps:11.0-preview AS runtime
WORKDIR /app
COPY --from=build /app/paperdotnet /app/libe_sqlite3.so /app/appsettings.json ./
# /data holds the SQLite database and the Data Protection keys.
RUN mkdir -p /data && chown $APP_UID /data
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Storage__DataPath=/data
VOLUME /data
EXPOSE 8080
USER $APP_UID
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 CMD ["/app/paperdotnet", "healthcheck"]
ENTRYPOINT ["/app/paperdotnet"]
