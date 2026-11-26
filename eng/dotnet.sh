#!/bin/sh
# Runs dotnet with telemetry off. If the .NET SDK was unpacked into the user profile
# instead of being installed system-wide, that copy is used.
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
LOCAL="${LOCALAPPDATA:-$HOME/AppData/Local}/Microsoft/dotnet"
if [ -x "$LOCAL/dotnet.exe" ]; then
  export DOTNET_ROOT="$LOCAL" DOTNET_MULTILEVEL_LOOKUP=0
  exec "$LOCAL/dotnet.exe" "$@"
fi
exec dotnet "$@"
