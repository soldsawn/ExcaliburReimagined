#!/bin/bash
# Excalibur Reimagined — Linux denemesi (USB'den açılan Ubuntu'da). Kullanım: bash /cdrom/dene.sh
# Uygulamayı kurar, fanları sırayla Performans, Sessiz ve Orijinal'e alıp ölçer, sonuçları USB'ye (olmazsa ev klasörüne) yazar.
OUT="$HOME/excalibur-deneme-$(date +%H%M)"
mkdir -p "$OUT"
exec > >(tee "$OUT/sonuc.txt") 2>&1
echo "== Excalibur Reimagined Linux denemesi $(date)"
uname -a
cat /sys/class/dmi/id/product_name /sys/class/dmi/id/bios_version 2>/dev/null
echo "lockdown: $(cat /sys/kernel/security/lockdown 2>/dev/null)"

PKG=$(ls /cdrom/ExcaliburReimagined-*-linux-x64.tar.gz /media/*/*/ExcaliburReimagined-*-linux-x64.tar.gz /run/media/*/*/ExcaliburReimagined-*-linux-x64.tar.gz 2>/dev/null | head -1)
if [ -z "$PKG" ]; then echo "!! Paket bulunamadı (USB'nin köküne kopyalandı mı?)"; exit 1; fi
echo "paket: $PKG"
rm -rf /tmp/exr && mkdir -p /tmp/exr && tar -xzf "$PKG" -C /tmp/exr
cd /tmp/exr/ExcaliburReimagined-*-linux-x64 || exit 1

echo; echo "== Kurulum"
sudo bash install.sh
echo; echo "ec_sys: $(sudo ls /sys/kernel/debug/ec/ec0/io 2>/dev/null || echo yok) write_support=$(cat /sys/module/ec_sys/parameters/write_support 2>/dev/null)"
echo "coretemp: $(grep -l coretemp /sys/class/hwmon/*/name 2>/dev/null | head -1)"
echo "EC ilk 256 bayt:"; sudo xxd /sys/kernel/debug/ec/ec0/io 2>/dev/null | head -16

step() { echo; echo "== $1"; [ -n "$2" ] && exr profile "$2" >/dev/null; for i in 1 2 3 4; do sleep 5; exr status | grep -E "Fan kontrolü|Profil|İşlemci|Ekran kartı"; echo "--"; done; }
step "Başlangıç (kayıtlı profil)" ""
step "Performans (boştayken %45, 50 °C üstünde hızlanır)" performans
step "Sessiz: fanlar yavaşlamalı" sessiz
step "Orijinal: dizüstünün kendi ayarı" orijinal
exr profile dengeli >/dev/null
echo; echo "== Son durum"; exr status
echo; echo "== Günlük"; sudo tail -40 /var/log/excalibur-reimagined/fan-*.log
sudo exr report >/dev/null 2>&1; sudo sh -c "cp /root/ExcaliburReimagined-rapor-*.zip '$OUT/'" 2>/dev/null

# Sonuçları USB'ye yaz (Ubuntu USB'yi salt okunur bağlar; yazılabilir yapmayı dener).
USB=$(dirname "$PKG")
sudo mount -o remount,rw "$USB" 2>/dev/null
if sudo cp -r "$OUT" "$USB/" 2>/dev/null; then echo; echo "Sonuçlar USB'ye yazıldı: $(basename "$OUT"). Windows'a dönebilirsiniz.";
else echo; echo "USB'ye yazılamadı; sonuçlar $OUT içinde. Bu ekranın fotoğrafını çekin."; fi
