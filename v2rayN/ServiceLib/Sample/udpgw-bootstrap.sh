#!/bin/sh
# Only packaged code and validated positional arguments enter this script.
set -eu
umask 077
target=
trap 'status=$?; if [ "$status" -ne 0 ]; then printf "V2RAYN_UDPGW_BOOTSTRAP_ERROR_%s\n" "$status"; fi; if [ -n "$target" ]; then rm -rf -- "$target"; fi' EXIT
trap 'exit 130' HUP INT TERM
id=$1
helper_hash=$2
binary_hash=$3
case "$id" in ''|*[!a-f0-9]*) exit 70;; esac
[ ${#id} -eq 32 ] || exit 70
for value in "$helper_hash" "$binary_hash"; do
    case "$value" in ''|*[!a-f0-9]*) exit 70;; esac
    [ ${#value} -eq 64 ] || exit 70
done
[ "$(id -u)" -eq 0 ] || exit 71
source=/tmp/v2rayn-udpgw-$id
[ ! -L "$source" ] && [ -d "$source" ] || exit 72
[ "$(stat -c %u "$source")" = "${SUDO_UID:-0}" ] || exit 72
[ "$(stat -c %a "$source")" = 700 ] || exit 72
# /run is commonly noexec. Stage executable code under the root-owned install
# parent, checking each component before creating anything there.
for directory in /opt /opt/v2rayn; do
    if [ ! -e "$directory" ]; then install -d -m 755 "$directory"; fi
    [ ! -L "$directory" ] && [ -d "$directory" ] || exit 72
    [ "$(stat -c %u "$directory")" = 0 ] || exit 72
    mode=$(stat -c %a "$directory")
    [ $((0$mode & 0022)) -eq 0 ] || exit 72
done
target=$(mktemp -d /opt/v2rayn/.udpgw-stage-XXXXXXXX) || exit 72
for file in helper udpgw-server; do
    [ ! -L "$source/$file" ] && [ -f "$source/$file" ] || exit 72
    install -m 700 "$source/$file" "$target/$file"
done
printf '%s  %s\n' "$helper_hash" "$target/helper" "$binary_hash" "$target/udpgw-server" | sha256sum -c --status || exit 73
# This marker prevents a cached/NOPASSWD sudo session consuming JSON as a password.
printf 'V2RAYN_UDPGW_READY\n'
"$target/helper"
