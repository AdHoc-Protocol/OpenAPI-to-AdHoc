#!/usr/bin/env bash
# Compiles the converter and converts every sample into AdHoc/.
#   ./build.sh            # build + convert samples/ → AdHoc/
set -eu
cd "$(dirname "$0")"
rm -rf out
mkdir -p AdHoc
dotnet build OpenAPI2AdHoc.csproj -c Release -o out --nologo -v quiet -clp:NoSummary
dotnet out/OpenAPI2AdHoc.dll samples AdHoc
