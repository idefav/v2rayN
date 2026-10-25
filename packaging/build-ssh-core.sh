#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
target_os="${1:?GOOS required}"
target_arch="${2:?GOARCH required}"
output="${3:?output path required}"
commit=e0ee4f251e66a29bda3c961ae11443c6cbf72978
build_dir="$(mktemp -d "${TMPDIR:-/tmp}/v2rayn-ssh-core.XXXXXX")"
trap 'rm -rf "$build_dir"' EXIT
git init --quiet "$build_dir/source"
git -C "$build_dir/source" remote add origin https://github.com/idefav/sing-box.git
git -C "$build_dir/source" fetch --quiet --depth 1 origin "$commit"
git -C "$build_dir/source" checkout --quiet --detach FETCH_HEAD
test "$(git -C "$build_dir/source" rev-parse HEAD)" = "$commit"
(cd "$build_dir/source" && go test -race ./protocol/ssh/...)
export GOOS="$target_os" GOARCH="$target_arch"
tags=with_gvisor,with_quic,with_dhcp,with_wireguard,with_utls,with_acme,with_clash_api,with_tailscale,with_ccm,with_ocm,with_cloudflared,with_naive_outbound,with_usbip,with_openvpn,with_openconnect,badlinkname,tfogo_checklinkname0
link_flags='-s -w -X github.com/sagernet/sing-box/constant.Version=1.14.3-ssh-udp.1'
if [[ "$target_os" == windows ]]; then
    export CGO_ENABLED=0
    tags="$tags,with_purego"
    link_flags="$link_flags -checklinkname=0"
else
    export CGO_ENABLED=1
    if [[ "$target_os" == darwin ]]; then
        if [[ "$target_arch" == amd64 ]]; then export CC="clang -arch x86_64" CXX="clang++ -arch x86_64";
        else export CC="clang -arch arm64" CXX="clang++ -arch arm64"; fi
    elif [[ "$target_os" == linux && "$target_arch" == arm64 && "$(uname -m)" != aarch64 ]]; then
        export CC=aarch64-linux-gnu-gcc CXX=aarch64-linux-gnu-g++
    fi
fi
mkdir -p "$(dirname "$output")"
output="$(cd "$(dirname "$output")" && pwd)/$(basename "$output")"
cd "$build_dir/source"
go build -trimpath -tags "$tags" -ldflags "$link_flags" -o "$output" ./cmd/sing-box
python3 "$root/packaging/write-ssh-core-manifest.py" "$output"
