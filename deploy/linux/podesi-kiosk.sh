#!/usr/bin/env bash
# Podešavanje paketomata posle prvog boot-a (instalacija sa ISO-a: kiosk je instaliran, ali bez konfiguracije mašine i
# bez boot-a u kiosk). Privremeno, dok ne stigne konačan način podešavanja. Pokreće root (sudo), lokalno ili preko SSH-a:
#   sudo /opt/toppos-kiosk/podesi-kiosk.sh [--config <app-configuration.json>] [--ime <ime računara>]
# Koraci (svaki se može preskočiti Enter-om kad je već podešen): konfiguracija (provera JSON-a), serijski portovi
# (kontroler vrata, POS, skener), ime računara, boot u kiosk.
set -euo pipefail

say() { printf '\n=== %s\n' "$*"; }
die() { printf '\nGREŠKA: %s\n' "$*" >&2; exit 1; }

source_config=
host=
while [ $# -gt 0 ]; do
	case "$1" in
		--config) source_config="$2"; shift 2 ;;
		--ime) host="$2"; shift 2 ;;
		*) die "Nepoznat argument: $1" ;;
	esac
done

[ "$(id -u)" -eq 0 ] || die "Pokrenuti kao root: sudo $0"
prefix=/opt/toppos-kiosk
etc=/etc/toppos-kiosk
config="$etc/app-configuration.json"
[ -x "$prefix/boot-mode.sh" ] || die "Kiosk nije instaliran ($prefix)."
command -v python3 > /dev/null || die "Nema python3 (apt-get install python3)."

# ─── 1. Konfiguracija ────────────────────────────────────────────────────────────────────────────────────────────────
# Izmene idu u privremenu kopiju; aktivna konfiguracija se zamenjuje (atomski) tek kad su izabrani i portovi, pa prekid
# (Ctrl+C) ne ostavlja ni novu konfiguraciju bez portova ni delimičan upis. U njoj su lozinke servisa i ključevi POS-a:
# čita je samo kiosk (grupa toppos).
work="$config.novo"
trap 'rm -f "$work"' EXIT
json_object() {
	python3 -c 'import json, sys; sys.exit(0 if isinstance(json.load(open(sys.argv[1], encoding="utf-8-sig")), dict) else 1)' \
		"$1" 2> /dev/null
}
if [ -n "$source_config" ]; then
	say "Konfiguracija iz $source_config"
	[ -f "$source_config" ] || die "Nema fajla $source_config."
	json_object "$source_config" || die "$source_config nije ispravna konfiguracija (JSON objekat)."
	install -m 640 -o root -g toppos "$source_config" "$work"
else
	[ -f "$config" ] || die "Nema $config — dati ga sa --config <fajl> (npr. sa USB-a)."
	json_object "$config" || die "$config nije ispravna konfiguracija (JSON objekat) — dati novu sa --config."
	install -m 640 -o root -g toppos "$config" "$work"
fi

# ─── 2. Serijski portovi (kao deploy/linux/hw-proba/pokreni-probu.sh) ────────────────────────────────────────────────
ports=()
covered=" "
for p in /dev/serial/by-id/*; do
	# Adapter izvučen tokom popisa: link nestaje, pa se port preskače umesto prekida (set -e).
	target=$(readlink -e -- "$p") || continue
	ports+=("$p")
	covered="$covered$target "
done
# USB čvorovi bez sopstvenog by-id imena (npr. dva ista adaptera dobiju isto by-id ime).
for p in /dev/ttyUSB* /dev/ttyACM*; do
	[ -e "$p" ] || continue
	case "$covered" in
		*" $p "*) ;;
		*) ports+=("$p") ;;
	esac
done
# Portovi na ploči (ttyS) koje kernel stvarno vidi, sa adresom i IRQ-om za poređenje sa BIOS-om (COM1..COM4).
declare -A details=()
while read -r line; do
	p="/dev/ttyS${line%%:*}"
	ports+=("$p")
	text=""
	if [[ "$line" =~ port:([0-9A-Fa-f]+) ]]; then
		address=$(printf '0x%X' $((16#${BASH_REMATCH[1]})))
		text="adresa $address"
		case "$address" in
			0x3F8) text="$text (obično COM1)" ;;
			0x2F8) text="$text (obično COM2)" ;;
			0x3E8) text="$text (obično COM3)" ;;
			0x2E8) text="$text (obično COM4)" ;;
		esac
	elif [[ "$line" =~ mmio:(0x[0-9A-Fa-f]+) ]]; then
		text="mmio ${BASH_REMATCH[1]}"
	fi
	[[ "$line" =~ irq:([0-9]+) ]] && text="$text, IRQ ${BASH_REMATCH[1]}"
	details[$p]=$text
done < <(grep -E '^[0-9]+:' /proc/tty/driver/serial 2> /dev/null | grep -v 'uart:unknown' || true)

current() {
	python3 - "$work" "$1" << 'PY'
import json, sys
c = json.load(open(sys.argv[1], encoding="utf-8-sig"))
for k in sys.argv[2].split("."):
	c = c.get(k) if isinstance(c, dict) else None
print(c if c is not None else "")
PY
}
# canon <putanja>: stvarni uređaj (by-id link i /dev/ttyUSB0 su isti port); prazno ostaje prazno.
canon() {
	if [ -n "$1" ]; then
		readlink -f -m -- "$1"
	fi
}
labels=("Kontroler vrata" "POS terminal" "Barkod skener")
keys=(Locker.ComPort.PortName PosTerminal.ComPort.PortName BarcodeScanner.ComPort.PortName)
nows=()
for key in "${keys[@]}"; do
	now=$(current "$key") || die "Konfiguracija nije ispravan JSON."
	nows+=("$now")
done

say "Serijski portovi"
if [ ${#ports[@]} -eq 0 ]; then
	echo "Nijedan serijski port nije nađen (uređaji priključeni?)."
fi
for i in "${!ports[@]}"; do
	p=${ports[$i]}
	target=$(canon "$p")
	line=$(printf '  %d) %s' $((i + 1)) "$p")
	[ "$target" != "$p" ] && line="$line -> $target"
	[ -n "${details[$p]:-}" ] && line="$line  ${details[$p]}"
	for j in "${!keys[@]}"; do
		[ "$(canon "${nows[$j]}")" = "$target" ] && line="$line  [sada: ${labels[$j]}]"
	done
	echo "$line"
done

# Jedan port može imati samo jedan uređaj. Poređenje i po putanji i po stvarnom uređaju.
choices=()
chosen=()
chosen_targets=()
for j in "${!keys[@]}"; do
	while :; do
		printf '%s [sada: %s] — broj sa spiska, 0 = bez porta, Enter = zadrži: ' "${labels[$j]}" \
			"${nows[$j]:-nije podešen}"
		read -r answer
		if [ -z "$answer" ]; then
			value=${nows[$j]}
		elif [ "$answer" = 0 ]; then
			value=""
		elif [[ "$answer" =~ ^[0-9]+$ ]] && [ "$answer" -ge 1 ] && [ "$answer" -le ${#ports[@]} ]; then
			value=${ports[$((answer - 1))]}
		else
			echo "  Pogrešan unos."
			continue
		fi
		target=$(canon "$value")
		taken=""
		for k in "${!chosen[@]}"; do
			if [ -n "$value" ] && { [ "${chosen[$k]}" = "$value" ] || [ "${chosen_targets[$k]}" = "$target" ] ||
				[ "$(canon "${chosen[$k]}")" = "$target" ]; }; then
				taken=${labels[$k]}
			fi
		done
		if [ -n "$taken" ]; then
			echo "  $value je već izabran za: $taken. Izabrati drugi port ili 0 (Ctrl+C prekida bez izmena)."
			continue
		fi
		choices+=("${keys[$j]}=$value")
		chosen+=("$value")
		chosen_targets+=("$target")
		break
	done
done

# Portovi u privremenu kopiju (vlasnik i prava ostaju), pa atomska zamena aktivne konfiguracije.
python3 - "$work" "${choices[@]}" << 'PY'
import json, os, sys, tempfile
path = sys.argv[1]
c = json.load(open(path, encoding="utf-8-sig"))
for choice in sys.argv[2:]:
	key, value = choice.split("=", 1)
	node = c
	*parents, last = key.split(".")
	for k in parents:
		# Sekcija koja nedostaje ili je null (npr. "ComPort": null) postaje prazan objekat.
		if not isinstance(node.get(k), dict):
			node[k] = {}
		node = node[k]
	node[last] = value
st = os.stat(path)
fd, tmp = tempfile.mkstemp(dir=os.path.dirname(path), prefix=".app-configuration-")
with os.fdopen(fd, "w", encoding="utf-8") as f:
	json.dump(c, f, indent=2, ensure_ascii=False)
	f.flush()
	os.fsync(f.fileno())
os.chown(tmp, st.st_uid, st.st_gid)
os.chmod(tmp, st.st_mode & 0o777)
os.replace(tmp, path)
PY
mv "$work" "$config"
trap - EXIT
echo "Konfiguracija upisana: $config"

# ─── 3. Ime računara ─────────────────────────────────────────────────────────────────────────────────────────────────
if [ -z "$host" ]; then
	printf '\nIme računara [sada: %s] (npr. oznaka paketomata), Enter = zadrži: ' "$(hostname)"
	read -r host
fi
if [ -n "$host" ] && [ "$host" != "$(hostname)" ]; then
	[[ "$host" =~ ^[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?$ ]] || die "Ime računara: slova, cifre i crtica (do 63 znaka)."
	old=$(hostname)
	# Bez D-Bus-a (sistem bez dbus paketa) hostnamectl ne radi; tada direktno.
	if ! hostnamectl set-hostname "$host" 2> /dev/null; then
		printf '%s\n' "$host" > /etc/hostname
		hostname "$host"
	fi
	# Debian instaler upisuje ime i u /etc/hosts (127.0.1.1); bez izmene sudo prijavljuje „unable to resolve host“.
	if grep -q "^127\.0\.1\.1[[:space:]]" /etc/hosts; then
		sed -i "s/^127\.0\.1\.1[[:space:]].*/127.0.1.1\t$host/" /etc/hosts
	else
		printf '127.0.1.1\t%s\n' "$host" >> /etc/hosts
	fi
	echo "Ime računara: $old -> $host"
fi

# ─── 4. Boot u kiosk ─────────────────────────────────────────────────────────────────────────────────────────────────
say "Boot u kiosk"
printf 'Pri sledećem boot-u pokretati kiosk? [D/n] '
read -r answer
case "$answer" in
	n|N|ne|NE) echo "Bez izmene režima. Kasnije: sudo $prefix/boot-mode.sh kiosk" ;;
	*)
		"$prefix/boot-mode.sh" kiosk
		echo "Restart: sudo systemctl reboot. Povratak: sudo $prefix/boot-mode.sh desktop (doc/deploy-linux.md)."
		;;
esac
