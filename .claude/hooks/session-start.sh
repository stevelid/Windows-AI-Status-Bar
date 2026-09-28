#!/bin/bash
# Installs the .NET 10 SDK in Claude Code cloud sessions so the solution builds and the
# core tests run. Does nothing on local machines.
set -euo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
  # The Ubuntu archive carries dotnet-sdk-10.0; dot.net install scripts may be blocked by the proxy.
  apt-get install -y -qq dotnet-sdk-10.0 >/dev/null 2>&1 || {
    apt-get update -qq >/dev/null 2>&1
    apt-get install -y -qq dotnet-sdk-10.0 >/dev/null 2>&1
  }
fi

cd "${CLAUDE_PROJECT_DIR:-.}"
dotnet restore WindowsAIStatusBar.slnx --nologo >/dev/null 2>&1 || true
echo "dotnet $(dotnet --version) ready. Build: dotnet build WindowsAIStatusBar.slnx | Test: dotnet test tests/StatusBar.Core.Tests"
