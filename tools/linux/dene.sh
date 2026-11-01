#!/bin/bash
# Excalibur Reimagined — Linux denemesi (0.2.0: .deb kurulumu, yeni pencere, ortak servis dili; USB'den açılan Ubuntu'da).
# Kullanım: bash /cdrom/dene.sh
OUT="$HOME/excalibur-deneme-$(date +%H%M)"
mkdir -p "$OUT"
exec > >(tee "$OUT/sonuc.txt") 2>&1
echo "== Excalibur Reimagined Linux denemesi $(date)"
uname -a
cat /sys/class/dmi/id/product_name /sys/class/dmi/id/bios_version 2>/dev/null
echo "lockdown: $(cat /sys/kernel/security/lockdown 2>/dev/null)"
# Linux keeps the hardware clock in UTC, Windows in local time: without this Windows boots 3 hours behind afterwards.
sudo timedatectl set-local-rtc 1 2>/dev/null
# The live session runs in UTC (no time zone chosen): show Turkish time, as an installed Ubuntu would.
sudo timedatectl set-timezone Europe/Istanbul 2>/dev/null

DEB=$(ls /cdrom/excalibur-reimagined_*_amd64.deb /media/*/*/excalibur-reimagined_*_amd64.deb /run/media/*/*/excalibur-reimagined_*_amd64.deb 2>/dev/null | tail -1)
if [ -z "$DEB" ]; then echo "!! .deb paketi bulunamadı (USB'nin köküne kopyalandı mı?)"; exit 1; fi
echo "paket: $DEB"
cp "$DEB" /tmp/exr.deb

echo; echo "== 1. Kurulum (.deb)"
sudo apt-get install -y /tmp/exr.deb 2>&1 | grep -v "^Reading\|^Building\|Reading database"
dpkg -s excalibur-reimagined | grep -E "^Status|^Version"
echo "servis: $(systemctl is-active excalibur-reimagined) / $(systemctl is-enabled excalibur-reimagined)"
exr version
sleep 3
echo; echo "ec_sys: $(sudo ls /sys/kernel/debug/ec/ec0/io 2>/dev/null || echo yok) write_support=$(cat /sys/module/ec_sys/parameters/write_support 2>/dev/null)"
echo "coretemp: $(grep -l coretemp /sys/class/hwmon/*/name 2>/dev/null | head -1)"
echo "intel-rapl: $(cat /sys/class/powercap/intel-rapl:0/name 2>/dev/null || echo yok) PL1=$(cat /sys/class/powercap/intel-rapl:0/constraint_0_power_limit_uw 2>/dev/null) PL2=$(cat /sys/class/powercap/intel-rapl:0/constraint_1_power_limit_uw 2>/dev/null)"
echo "platform_profile: $(cat /sys/firmware/acpi/platform_profile 2>/dev/null || echo yok)"
echo "power_supply: $(ls /sys/class/power_supply/ 2>/dev/null | tr '\n' ' ')"

echo; echo "== 2. Güç Modları (Ofis -> Yüksek performans -> Oyun)"
for m in ofis yuksek oyun; do
  echo "-- mode $m"
  exr mode "$m" | grep -E "Güç modu|İşlemci|Ekran kartı"
  sleep 2
  echo "   EC 0x95=$(sudo xxd -s $((0x95)) -l 1 -p /sys/kernel/debug/ec/ec0/io 2>/dev/null) RAPL_PL1(MSR)=$(cat /sys/class/powercap/intel-rapl:0/constraint_0_power_limit_uw 2>/dev/null) PL1(MMIO, güç modunun)=$(cat /sys/class/powercap/intel-rapl-mmio:0/constraint_0_power_limit_uw 2>/dev/null || echo yok)"
done

echo; echo "== 3. İşlemci Güç Sınırı (35/60 W -> Kaldır)"
exr cpu-limit 35 60 | grep "İşlemci:"
echo "   RAPL_PL1=$(cat /sys/class/powercap/intel-rapl:0/constraint_0_power_limit_uw 2>/dev/null) RAPL_PL2=$(cat /sys/class/powercap/intel-rapl:0/constraint_1_power_limit_uw 2>/dev/null)"
exr cpu-limit off | grep "İşlemci:"
echo "   Geri yüklendi: RAPL_PL1=$(cat /sys/class/powercap/intel-rapl:0/constraint_0_power_limit_uw 2>/dev/null) RAPL_PL2=$(cat /sys/class/powercap/intel-rapl:0/constraint_1_power_limit_uw 2>/dev/null)"

echo; echo "== 4. Klavye Işığı (3 Bölge Kırmızı/Yeşil/Mavi -> Camgöbeği)"
exr light zones "#FF0000" "#00FF00" "#0000FF" --level 2 | grep "Klavye ışığı:"
sleep 3
exr light "#00FFFF" --level 2 | grep "Klavye ışığı:"
echo "   EC LED (0x89..0x8B, 0xC1, 0xAC, 0x48): R=$(sudo xxd -s $((0x89)) -l 3 -p /sys/kernel/debug/ec/ec0/io 2>/dev/null) C1=$(sudo xxd -s $((0xC1)) -l 1 -p /sys/kernel/debug/ec/ec0/io 2>/dev/null) AC=$(sudo xxd -s $((0xAC)) -l 1 -p /sys/kernel/debug/ec/ec0/io 2>/dev/null) 48=$(sudo xxd -s $((0x48)) -l 1 -p /sys/kernel/debug/ec/ec0/io 2>/dev/null)"

echo; echo "== 5. Özel Profil ve Otomasyon"
exr profile save Gece 45:25,60:35,70:60,80:100 >/dev/null
exr profiles
exr profile dengeli >/dev/null
exr profile delete Gece >/dev/null

echo; echo "== 6. Pil ve Geçmiş"
exr battery
echo
exr history 5

echo; echo "== 7. Son Durum ve Günlük"
exr status
echo
sudo tail -40 /var/log/excalibur-reimagined/fan-*.log
sudo exr report >/dev/null 2>&1; sudo sh -c "cp /root/ExcaliburReimagined-rapor-*.zip '$OUT/'" 2>/dev/null

# Sonuçları USB'ye yaz (Ubuntu USB'yi salt okunur bağlar; yazılabilir yapmayı dener).
USB=$(dirname "$DEB")
sudo mount -o remount,rw "$USB" 2>/dev/null
if sudo cp -r "$OUT" "$USB/" 2>/dev/null; then
  echo; echo "== Sonuçlar USB'ye yazıldı: $(basename "$OUT")"
else
  echo; echo "== USB'ye yazılamadı; sonuçlar $OUT içinde."
fi

echo; echo "== 8. Pencere ekran görüntüleri (gerçek cihazda, birkaç saniye sürer)"
echo "tepsi desteği: $(dbus-send --session --print-reply --dest=org.freedesktop.DBus /org/freedesktop/DBus org.freedesktop.DBus.NameHasOwner string:org.kde.StatusNotifierWatcher 2>/dev/null | grep -o 'boolean [a-z]*')"
timeout 90 exr gui --snapshot "$OUT/ekran" >/dev/null 2>&1; echo "görüntüler: $(ls "$OUT/ekran" 2>/dev/null | tr '\n' ' ')"
if sudo cp -r "$OUT" "$USB/" 2>/dev/null; then echo "(ekran görüntüleri de USB'ye yazıldı)"; fi

echo; echo "== Masaüstü penceresi açılıyor (uygulama menüsünde de 'Excalibur Reimagined' var)..."
echo "   Kaldırmak isterseniz: sudo apt remove excalibur-reimagined"
nohup exr gui >/dev/null 2>&1 &
