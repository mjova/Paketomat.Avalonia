#!/usr/bin/env bash
# Skuplja sve za analizu probe u jedan fajl pored skripte: log i stanje programa, log simulatora, konfiguraciju (bez
# ključeva POS-a), podatke o ekranu, ulazu, zvuku i serijskim uređajima. Ništa ne menja.
# ECR log (logovi-kiosk/ECR) sadrži sirove odgovore POS terminala, sa mogućim podacima kartice, pa ulazi samo uz --sa-ecr.
#   ./prikupi-logove.sh [--sa-ecr]
set -uo pipefail
cd "$(dirname "$0")"

with_ecr=0
[ "${1:-}" = "--sa-ecr" ] && with_ecr=1

name="logovi-$(hostname)-$(date +%Y%m%d-%H%M%S)"
tmp=$(mktemp -d)
out="$tmp/$name"
mkdir -p "$out"

fail() {
	rm -rf "$tmp"
	echo "$1 — arhiva NIJE napravljena (pun disk?)." >&2
	exit 1
}

# copy <izvor> <cilj>: izvor koji ne postoji se preskače; neuspelo kopiranje postojećeg prekida skupljanje.
copy() {
	[ -e "$1" ] || return 0
	cp -r "$1" "$2" || fail "Kopiranje $1 nije uspelo"
}

run() {
	# run <fajl> <komanda...>: izlaz i greške u fajl; komanda koja ne postoji ne prekida skupljanje.
	local file="$1"
	shift
	{ echo "\$ $*"; "$@"; } > "$out/$file" 2>&1 || true
}

mkdir -p "$out/kiosk-log"
for f in logovi-kiosk/*; do
	if [ "$(basename "$f")" != ECR ] || [ "$with_ecr" -eq 1 ]; then
		copy "$f" "$out/kiosk-log/"
	fi
done
copy stanje "$out/stanje"
copy kiosk/paketomat.ver "$out/"
copy simulatori/Logs "$out/simulatori-log"

# Konfiguracija bez PosTerminal.ProductKey (JSON parserom); bez python3 ili uz grešku ne ulazi u arhivu.
if command -v python3 > /dev/null && python3 - kiosk-proba.json "$out/kiosk-proba.json" << 'PY'
import json, sys
c = json.load(open(sys.argv[1], encoding="utf-8-sig"))
c.get("PosTerminal", {}).pop("ProductKey", None)
json.dump(c, open(sys.argv[2], "w", encoding="utf-8"), indent=2, ensure_ascii=False)
PY
then
	:
else
	rm -f "$out/kiosk-proba.json"
	echo "Konfiguracija nije uključena (python3 nije dostupan ili JSON nije ispravan)." >&2
fi

run sistem.txt sh -c 'cat /etc/os-release; uname -a; echo "sesija: $XDG_SESSION_TYPE $XDG_CURRENT_DESKTOP DISPLAY=$DISPLAY"; id'
run ekran.txt sh -c 'lspci -k 2>/dev/null | grep -A3 -Ei "vga|display|3d"; for c in /sys/class/drm/card*-*; do echo "$c: $(cat $c/status 2>/dev/null) $(head -n 3 $c/modes 2>/dev/null | tr "\n" " ")"; done; xrandr 2>/dev/null'
run ulaz.txt sh -c 'cat /proc/bus/input/devices; ls -l /dev/input/by-id 2>/dev/null'
run zvuk.txt sh -c 'cat /proc/asound/cards; pactl info 2>/dev/null | head -n 15'
run serijski.txt sh -c 'ls -l /dev/serial/by-id /dev/ttyACM* /dev/ttyUSB* 2>/dev/null; systemctl is-enabled ModemManager 2>&1; lsusb 2>/dev/null'
run kernel.txt sh -c 'journalctl -k -b --no-pager 2>/dev/null | grep -Ei "tty|usb|serial|cdc_acm|input|touch" | tail -n 200 || dmesg 2>&1 | tail -n 200'
run procesi.txt ps -eo pid,ppid,user,etimes,stat,comm

tar -C "$tmp" -czf "$name.tar.gz.part" "$name" || { rm -f "$name.tar.gz.part"; fail "Pravljenje arhive nije uspelo"; }
mv "$name.tar.gz.part" "$name.tar.gz" || fail "Preimenovanje arhive nije uspelo"
rm -rf "$tmp"
echo "Logovi: $(pwd)/$name.tar.gz"
