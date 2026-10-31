#!/bin/bash
# Excalibur Reimagined (Linux) kurulumu. Kullanım: sudo bash install.sh
set -e
APP=excalibur-reimagined
DIR=/opt/$APP
HERE="$(cd "$(dirname "$0")" && pwd)"

if [ "$(id -u)" -ne 0 ]; then echo "Bu betik yönetici yetkisi ister: sudo bash install.sh"; exit 1; fi
if [ ! -f "$HERE/$APP" ]; then echo "$APP dosyası bu klasörde yok; paketi açtığınız klasörden çalıştırın."; exit 1; fi

echo "== Dosyalar: $DIR"
mkdir -p "$DIR" /var/lib/$APP /var/log/$APP
install -m 755 "$HERE/$APP" "$DIR/$APP"
install -m 755 "$HERE/uninstall.sh" "$DIR/uninstall.sh"
ln -sf "$DIR/$APP" /usr/local/bin/$APP
# Kısa yol: yalnız başka bir şeye ait değilse.
if ! command -v exr >/dev/null 2>&1 || [ "$(readlink -f "$(command -v exr)")" = "$DIR/$APP" ]; then ln -sf "$DIR/$APP" /usr/local/bin/exr; fi

echo "== EC erişimi (ec_sys, yazma açık)"
echo "options ec_sys write_support=1" > /etc/modprobe.d/$APP.conf
echo "ec_sys" > /etc/modules-load.d/$APP.conf
modprobe -r ec_sys 2>/dev/null || true
modprobe ec_sys write_support=1 || echo "UYARI: ec_sys yüklenemedi (bu çekirdekte olmayabilir)."
mountpoint -q /sys/kernel/debug || mount -t debugfs none /sys/kernel/debug || true

echo "== Servis"
cat > /etc/systemd/system/$APP.service <<UNIT
[Unit]
Description=Excalibur Reimagined fan control
After=systemd-modules-load.service

[Service]
ExecStart=$DIR/$APP service
# Servis durunca ya da çökünce fanlar dizüstünün kendi ayarına döner.
ExecStopPost=$DIR/$APP release
Restart=always
RestartSec=3

[Install]
WantedBy=multi-user.target
UNIT
mkdir -p /usr/lib/systemd/system-sleep
cat > /usr/lib/systemd/system-sleep/$APP <<'HOOK'
#!/bin/sh
# Uykudan önce fanlar dizüstüne bırakılır, uyanınca fan kontrolü yeniden başlar.
case "$1" in
  pre)  /usr/local/bin/excalibur-reimagined sleep pre ;;
  post) /usr/local/bin/excalibur-reimagined sleep post ;;
esac
HOOK
chmod 755 /usr/lib/systemd/system-sleep/$APP
systemctl daemon-reload
systemctl enable --now $APP.service
sleep 3

echo
if grep -q "\[none\]" /sys/kernel/security/lockdown 2>/dev/null || [ ! -f /sys/kernel/security/lockdown ]; then :; else
  echo "!! Secure Boot açık: Linux bu modda EC'ye yazmaya izin vermiyor. Fan kontrolü için BIOS'ta (açılışta F2 > Security) Secure Boot'u kapatın."
fi
$APP status || true
echo
echo "Kuruldu. Komutlar: $APP status | $APP profile sessiz|dengeli|performans|orijinal | $APP report"
echo "Kaldırmak için: sudo $DIR/uninstall.sh"
