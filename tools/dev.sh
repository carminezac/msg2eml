#!/usr/bin/env bash
# Development commands without .NET on the host: everything runs in the SDK image.
#
#   ./tools/dev.sh test            runs the tests (uses samples/ if present)
#   ./tools/dev.sh build           builds the msg2eml:local image
#   ./tools/dev.sh up              docker compose up -d --build
#   ./tools/dev.sh down            docker compose down
#   ./tools/dev.sh sdk <cmd...>    arbitrary dotnet command in the SDK container
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SDK_IMAGE="mcr.microsoft.com/dotnet/sdk:10.0"
NUGET_CACHE="${NUGET_CACHE:-$HOME/.cache/msg2eml-nuget}"
mkdir -p "$NUGET_CACHE"

sdk() {
  docker run --rm \
    --user "$(id -u):$(id -g)" \
    -e HOME=/tmp/home \
    -e DOTNET_CLI_HOME=/tmp/home \
    -e NUGET_PACKAGES=/nuget \
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    -e DOTNET_NOLOGO=1 \
    -e MSG2EML_SAMPLES="${MSG2EML_SAMPLES:-/src/samples}" \
    -v "$NUGET_CACHE":/nuget \
    -v "$ROOT_DIR":/src \
    -w /src \
    "$SDK_IMAGE" "$@"
}

case "${1:-}" in
  test)
    shift
    sdk dotnet test tests/Msg2Eml.Tests/Msg2Eml.Tests.csproj --logger "console;verbosity=normal" "$@"
    ;;
  build)
    docker build -t msg2eml:local "$ROOT_DIR"
    ;;
  up)
    docker compose -f "$ROOT_DIR/docker-compose.yml" up -d --build
    ;;
  down)
    docker compose -f "$ROOT_DIR/docker-compose.yml" down
    ;;
  sdk)
    shift
    sdk "$@"
    ;;
  *)
    sed -n '2,9p' "$0"
    exit 2
    ;;
esac
