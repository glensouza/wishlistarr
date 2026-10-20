#!/bin/sh
set -eu

PUID="${PUID:-1000}"
PGID="${PGID:-1000}"
DATA_DIR="${WISHLISTARR_DATA_DIR:-/data}"

if [ "$(id -u)" -ne 0 ]; then
    echo "entrypoint must start as root to apply PUID/PGID" >&2
    exit 1
fi

case "$PUID$PGID" in
    *[!0-9]*) echo "PUID and PGID must be numeric" >&2; exit 1 ;;
esac

if [ "$PUID" -eq 0 ] || [ "$PGID" -eq 0 ]; then
    echo "PUID and PGID must not be 0; the app does not run as root" >&2
    exit 1
fi

case "${TZ:-}" in
    *..*) echo "TZ must not contain '..'" >&2; exit 1 ;;
esac

if [ -n "${TZ:-}" ]; then
    if [ -f "/usr/share/zoneinfo/$TZ" ]; then
        ln -sf "/usr/share/zoneinfo/$TZ" /etc/localtime
        echo "$TZ" > /etc/timezone
    else
        echo "warning: time zone '$TZ' not found, using UTC" >&2
    fi
fi

group_name="$(getent group "$PGID" | cut -d: -f1 || true)"
if [ -z "$group_name" ]; then
    group_name=wishlistarr
    groupadd -g "$PGID" "$group_name"
fi

user_name="$(getent passwd "$PUID" | cut -d: -f1 || true)"
if [ -z "$user_name" ]; then
    user_name=wishlistarr
    useradd -u "$PUID" -g "$PGID" -M -d "$DATA_DIR" -s /usr/sbin/nologin "$user_name"
fi

mkdir -p "$DATA_DIR"
chown -R "$PUID:$PGID" "$DATA_DIR"

export HOME="$DATA_DIR"
exec setpriv --reuid="$PUID" --regid="$PGID" --clear-groups "$@"
