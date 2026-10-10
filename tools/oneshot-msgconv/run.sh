#!/usr/bin/env bash
# Converts a .msg to .eml inside a throwaway container and prints a fidelity report.
#
#   ./tools/oneshot-msgconv/run.sh <input.msg> [output.eml]
#
# Default output: out/<input-name>.eml in the project root.
# The image is built on first run; to force a rebuild: REBUILD=1 ./run.sh ...
set -euo pipefail

IMAGE="msg2eml-oneshot:local"
TOOL_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$TOOL_DIR/../.." && pwd)"

if [[ $# -lt 1 ]]; then
  echo "usage: $0 <input.msg> [output.eml]" >&2
  exit 2
fi

IN="$(readlink -f "$1")"
[[ -f "$IN" ]] || { echo "file not found: $1" >&2; exit 2; }

if [[ $# -ge 2 ]]; then
  OUT="$(readlink -f "$2")"
else
  base="$(basename "$IN")"
  OUT="$ROOT_DIR/out/${base%.*}.eml"
fi
mkdir -p "$(dirname "$OUT")"

if [[ "${REBUILD:-0}" == "1" ]] || ! docker image inspect "$IMAGE" >/dev/null 2>&1; then
  echo ">> building image $IMAGE" >&2
  docker build -q -t "$IMAGE" "$TOOL_DIR" >/dev/null
fi

docker run --rm \
  --user "$(id -u):$(id -g)" \
  --read-only \
  --security-opt no-new-privileges:true \
  --cap-drop ALL \
  --network none \
  --tmpfs /tmp:size=64m \
  -v "$(dirname "$IN")":/in:ro \
  -v "$(dirname "$OUT")":/out \
  "$IMAGE" \
  "/in/$(basename "$IN")" "/out/$(basename "$OUT")"

echo ">> written: $OUT" >&2
