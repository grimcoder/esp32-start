#!/bin/zsh
set -e
SCRIPT_DIR="${0:A:h}"
dotnet build "$SCRIPT_DIR/BleClient.csproj" -c Debug --nologo -v q
exec "$SCRIPT_DIR/bin/Debug/net9.0-macos15.0/osx-arm64/BleClient.app/Contents/MacOS/BleClient"
