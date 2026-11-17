#!/bin/sh
set -eu
umask 077
id=$1
hash=$2
case "$id$hash" in ''|*[!a-f0-9]*) exit 70;; esac
[ ${#id} -eq 32 ] && [ ${#hash} -eq 64 ] && [ "$(id -u)" -eq 0 ] || exit 70
source=/tmp/v2rayn-dns-$id
[ ! -L "$source" ] && [ -d "$source" ] && [ "$(stat -c %a "$source")" = 700 ] || exit 71
[ "$(stat -c %u "$source")" = "${SUDO_UID:-0}" ] || exit 71
[ ! -L "$source/helper.py" ] && [ -f "$source/helper.py" ] || exit 71
for directory in /opt /opt/v2rayn; do
    if [ ! -e "$directory" ]; then install -d -m 755 "$directory"; fi
    [ ! -L "$directory" ] && [ -d "$directory" ] && [ "$(stat -c %u "$directory")" = 0 ] || exit 72
    mode=$(stat -c %a "$directory")
    [ $((0$mode & 0022)) -eq 0 ] || exit 72
done
target=$(mktemp -d /opt/v2rayn/.dns-stage-XXXXXXXX)
trap 'rm -rf -- "$target"' EXIT
install -m 700 "$source/helper.py" "$target/helper.py"
printf '%s  %s\n' "$hash" "$target/helper.py" | sha256sum -c --status || exit 73
/usr/bin/python3 -I -u "$target/helper.py"
