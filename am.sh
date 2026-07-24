#!/usr/bin/env bash
# Builds once, then runs the compiled binary directly (not 'dotnet run') so
# Ctrl+C / SIGINT reaches the app process itself. 'dotnet run' is a
# build-and-launch wrapper that does not reliably forward POSIX signals to
# the process it spawns, which breaks 'track's graceful-stop handling -
# 'kill -INT' on the dotnet-run PID does nothing; only SIGKILL gets through.
set -e
dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
dotnet build "$dir/AutomowerConsole/AutomowerConsole.csproj" -v quiet
exec dotnet "$dir/AutomowerConsole/bin/Debug/net10.0/AutomowerConsole.dll" "$@"
