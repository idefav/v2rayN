#!/usr/bin/env bash
set -euo pipefail
task_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
output=${1:?usage: build-udpgw-helpers.sh OUTPUT_DIRECTORY}
mkdir -p "$output/udpgw-deploy"
output=$(cd "$output/udpgw-deploy" && pwd)
cd "$task_root/proxy-check"
for arch in amd64 arm64; do
  CGO_ENABLED=0 GOOS=linux GOARCH=$arch go build -trimpath -ldflags='-s -w' -o "$output/udpgw-deploy-linux-$arch" ./cmd/udpgw-deploy
done
cd "$output"
if command -v sha256sum >/dev/null 2>&1; then
  sha256sum udpgw-deploy-linux-amd64 udpgw-deploy-linux-arm64 > SHA256SUMS
else
  shasum -a 256 udpgw-deploy-linux-amd64 udpgw-deploy-linux-arm64 > SHA256SUMS
fi
