#!/bin/bash
# Builds the .deb from the published Linux folder. Run by tools/scripts/make-linux.ps1 through WSL (dpkg-deb sets root
# ownership and the permissions; a Windows tar cannot). Usage: make-deb.sh <published-dir> <version> <output.deb>
set -euo pipefail
SRC="$1"; VERSION="$2"; OUT="$3"
HERE="$(cd "$(dirname "$0")" && pwd)"
APP=excalibur-reimagined
ROOT="$(mktemp -d)"
trap 'rm -rf "$ROOT"' EXIT

install -D -m 755 "$SRC/$APP" "$ROOT/opt/$APP/$APP"
install -D -m 644 "$SRC/BENI-OKU.txt" "$ROOT/usr/share/doc/$APP/BENI-OKU.txt"
mkdir -p "$ROOT/usr/bin"
ln -s "/opt/$APP/$APP" "$ROOT/usr/bin/$APP"
install -D -m 644 "$HERE/deb/$APP.service" "$ROOT/lib/systemd/system/$APP.service"
install -D -m 755 "$HERE/deb/sleep-hook" "$ROOT/usr/lib/systemd/system-sleep/$APP"
install -D -m 644 "$HERE/deb/$APP.desktop" "$ROOT/usr/share/applications/$APP.desktop"
mkdir -p "$ROOT/usr/share/icons/hicolor/256x256/apps"
"$ROOT/opt/$APP/$APP" --extract-icon "$ROOT/usr/share/icons/hicolor/256x256/apps/$APP.png"
mkdir -p "$ROOT/etc/modprobe.d" "$ROOT/etc/modules-load.d"
echo "options ec_sys write_support=1" > "$ROOT/etc/modprobe.d/$APP.conf"
echo "ec_sys" > "$ROOT/etc/modules-load.d/$APP.conf"

mkdir -p "$ROOT/DEBIAN"
SIZE=$(du -sk --exclude=DEBIAN "$ROOT" | cut -f1)
sed -e "s/@VERSION@/$VERSION/" -e "s/@SIZE@/$SIZE/" "$HERE/deb/control" > "$ROOT/DEBIAN/control"
printf '/etc/modprobe.d/%s.conf\n/etc/modules-load.d/%s.conf\n' "$APP" "$APP" > "$ROOT/DEBIAN/conffiles"
for s in preinst postinst prerm postrm; do install -m 755 "$HERE/deb/$s" "$ROOT/DEBIAN/$s"; done
# Linux line endings, whatever git checked out on Windows (a CR breaks the scripts' first line).
find "$ROOT/DEBIAN" "$ROOT/lib" "$ROOT/usr/lib" "$ROOT/usr/share/applications" -type f -exec sed -i 's/\r$//' {} +

dpkg-deb --root-owner-group --build "$ROOT" "$OUT" >/dev/null
echo "deb: $OUT ($(du -h "$OUT" | cut -f1))"
