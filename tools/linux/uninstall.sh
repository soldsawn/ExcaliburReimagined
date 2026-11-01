#!/bin/bash
# Excalibur Reimagined (Linux) kaldırma. Kullanım: sudo /opt/excalibur-reimagined/uninstall.sh
APP=excalibur-reimagined
DIR=/opt/$APP
if [ "$(id -u)" -ne 0 ]; then echo "Bu betik yönetici yetkisi ister: sudo $0"; exit 1; fi
systemctl disable --now $APP.service 2>/dev/null
[ -x "$DIR/$APP" ] && "$DIR/$APP" release 2>/dev/null
rm -f /etc/systemd/system/$APP.service /usr/lib/systemd/system-sleep/$APP /etc/modprobe.d/$APP.conf /etc/modules-load.d/$APP.conf
rm -f /usr/share/applications/$APP.desktop /usr/share/pixmaps/$APP.png
[ "$(readlink -f /usr/local/bin/exr 2>/dev/null)" = "$DIR/$APP" ] && rm -f /usr/local/bin/exr
rm -f /usr/local/bin/$APP
systemctl daemon-reload
read -r -p "Ayarlar ve günlükler de silinsin mi? (/var/lib/$APP, /var/log/$APP) [e/H] " ans
case "$ans" in e|E|evet) rm -rf /var/lib/$APP /var/log/$APP ;; esac
rm -rf "$DIR"
echo "Kaldırıldı; fanlar dizüstünün kendi ayarında."
