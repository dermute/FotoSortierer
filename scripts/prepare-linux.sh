#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."
export DOTNET_ROOT=/tmp/photorename-dotnet
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_HOME=/tmp/photorename-dotnet-home
export NUGET_PACKAGES=/tmp/photorename-nuget
export XDG_RUNTIME_DIR=/tmp/photorename-runtime
export DOTNET_CLI_TELEMETRY_OPTOUT=1
mkdir -p "$XDG_RUNTIME_DIR"
chmod 700 "$XDG_RUNTIME_DIR"

if [[ ! -x "$DOTNET_ROOT/dotnet" ]]; then
    echo 'SDK fehlt unter /tmp/photorename-dotnet.' >&2
    exit 1
fi

dotnet --version
dotnet restore tests/FotoSortierer.Tests/FotoSortierer.Tests.csproj
dotnet restore src/FotoSortierer.App/FotoSortierer.App.csproj \
    -r win-x64 -p:SelfContained=true -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false

curl -fsSL --retry 2 --connect-timeout 20 \
    https://raw.githubusercontent.com/drewnoakes/metadata-extractor-dotnet/main/LICENSE \
    -o /tmp/photorename-metadata-license.txt

echo 'Abhängigkeiten bereit. Das Projekt kann jetzt offline gebaut werden.'
