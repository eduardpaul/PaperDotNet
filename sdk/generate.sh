#!/usr/bin/env bash
# Generates the PaperDotNet SDKs (API-03) from sdk/openapi.json with Kiota (MIT).
# Update the document first: PAPERDOTNET_UPDATE_OPENAPI=1 dotnet test ... (see OpenApiDocumentTests).
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet tool restore >/dev/null

generate() { # language, output, namespace
  rm -rf "$2"
  dotnet kiota generate --openapi sdk/openapi.json --language "$1" --class-name PaperDotNetApiClient \
    --namespace-name "$3" --output "$2" --clean-output --exclude-backward-compatible --log-level Warning
}

generate CSharp src/Sdk/PaperDotNet.Client/Generated PaperDotNet.Client
generate TypeScript sdk/typescript/src/generated paperdotnet
generate Python sdk/python/paperdotnet_client/generated paperdotnet_client.generated
touch sdk/python/paperdotnet_client/generated/__init__.py
