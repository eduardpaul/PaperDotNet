# syntax=docker/dockerfile:1
# PaperDotNet API: one image, runs the API or admin commands (`paperdotnet migrate`, …).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props PaperDotNet.slnx .editorconfig ./
COPY src/ src/
RUN dotnet restore src/PaperDotNet.Host/PaperDotNet.Host.csproj
RUN dotnet publish src/PaperDotNet.Host/PaperDotNet.Host.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# OCR (DOC-07): the Tesseract CLI with English and German; add more tesseract-ocr-<lang> packages as needed.
# libfontconfig1 is needed by SkiaSharp (page rendering).
RUN apt-get update \
    && apt-get install -y --no-install-recommends tesseract-ocr tesseract-ocr-eng tesseract-ocr-deu libfontconfig1 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
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
