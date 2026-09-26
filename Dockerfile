# syntax=docker/dockerfile:1
# PaperDotNet: one image with the API and the web UI; runs the server or admin commands (`paperdotnet migrate`, …).

# The web UI (ADR-0033): the TypeScript SDK and the app, built with Node; only the static files are kept.
FROM node:22-bookworm-slim AS web
WORKDIR /src
COPY package.json package-lock.json ./
COPY sdk/typescript/package.json sdk/typescript/
COPY web/package.json web/
RUN npm ci --no-audit --no-fund
COPY sdk/typescript/ sdk/typescript/
COPY web/ web/
RUN npm run build -w @paperdotnet/web

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props PaperDotNet.slnx .editorconfig ./
COPY src/ src/
RUN dotnet restore src/PaperDotNet.Host/PaperDotNet.Host.csproj
RUN dotnet publish src/PaperDotNet.Host/PaperDotNet.Host.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# OCR (DOC-07): the Tesseract CLI with English and German; add more tesseract-ocr-<lang> packages as needed.
# libfontconfig1 is needed by SkiaSharp (page rendering). postgresql-client (pg_dump/pg_restore, v17 on
# Debian 13) is used by `paperdotnet backup|restore` with PostgreSQL; it must match the server or be newer.
RUN apt-get update \
    && apt-get install -y --no-install-recommends tesseract-ocr tesseract-ocr-eng tesseract-ocr-deu libfontconfig1 postgresql-client \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
# The host serves the UI from wwwroot (same origin as the API); list its callback in Auth:FirstPartyRedirectUris.
COPY --from=web /src/web/dist ./wwwroot
# /data holds the SQLite database (default) and the stored files (/data/blobs).
RUN mkdir -p /data && chown $APP_UID /data
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    PAPERDOTNET__Storage__DataPath=/data
VOLUME /data
EXPOSE 8080
USER $APP_UID
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 CMD ["dotnet", "/app/paperdotnet.dll", "healthcheck"]
ENTRYPOINT ["dotnet", "/app/paperdotnet.dll"]
