#!/usr/bin/env bash
# Sve iz UPUTSTVO.md jednom komandom, iz terminala na desktopu (kao običan korisnik; za sistemske korake pita sudo lozinku):
#   bash pokreni-probu.sh            priprema (jednom), RustDesk (jednom), izbor portova, pokretanje programa
#   bash pokreni-probu.sh logovi     logovi programa za USB (bez ECR loga)
# Pored skripte: toppos-kiosk-*-linux-x64.tar.gz i app-configuration.json sa test paketomata.
# U ~/toppos: program po izdanju (toppos-kiosk-*/app) i stalni folderi konfiguracija/, stanje/ (status.lock, redovi
# potvrda) i logovi/ — nov paket ih ne menja, pa se neposlate potvrde i podešeni portovi ne gube.
set -euo pipefail
kit=$(cd "$(dirname "$0")" && pwd)
root="$HOME/toppos"
config_dir="$root/konfiguracija"
state_dir="$root/stanje"
log_dir="$root/logovi"

say() { printf '\n=== %s\n' "$*"; }
die() { printf '\nGREŠKA: %s\n' "$*" >&2; exit 1; }

if [ "$(id -u)" -eq 0 ]; then
	die "Pokrenuti kao običan korisnik (bez sudo): bash pokreni-probu.sh"
fi

# ─── Logovi za USB ───────────────────────────────────────────────────────────────────────────────────────────────────
if [ "${1:-}" = "logovi" ]; then
	ls "$log_dir"/log-*.txt > /dev/null 2>&1 || die "Nema logova u $log_dir."
	name="logovi-$(hostname)-$(date +%Y%m%d-%H%M%S).tar.gz"
	# ECR log (logovi/ECR) može da sadrži podatke kartice — ostaje na mašini. Obrazac se širi tek u $root.
	(
		cd "$root"
		files=(logovi/log-*.txt)
		[ -f stanje/status.lock ] && files+=(stanje/status.lock)
		tar -czf "$kit/$name.part" "${files[@]}"
	) || { rm -f "$kit/$name.part"; die "Pravljenje arhive nije uspelo."; }
	mv "$kit/$name.part" "$kit/$name"
	say "Logovi: $kit/$name"
	exit 0
fi

[ -n "${DISPLAY:-}" ] || die "Nema DISPLAY: pokrenuti iz terminala na desktopu (ne preko SSH-a)."
count=$(ls "$kit"/toppos-kiosk-*-linux-x64.tar.gz 2> /dev/null | wc -l)
[ "$count" -eq 1 ] || die "Pored skripte mora biti tačno jedan toppos-kiosk-*-linux-x64.tar.gz (ima $count)."
archive=$(ls "$kit"/toppos-kiosk-*-linux-x64.tar.gz)
[ -f "$kit/app-configuration.json" ] || [ -f "$config_dir/app-configuration.json" ] \
	|| die "Pored skripte nema app-configuration.json (sa test paketomata)."

# ─── 1. Priprema sistema (preskače se ono što je već urađeno) ────────────────────────────────────────────────────────
icu=$(apt-cache search --names-only '^libicu[0-9]+$' 2> /dev/null | awk '{ print $1 }' | sort -V | tail -n 1)
packages="$icu libvlc5 vlc-plugin-base libfontconfig1 xwayland libx11-6 libice6 libsm6 libxcursor1 libxext6 libxfixes3
	libxi6 libxrandr2 libgl1 python3 curl"
missing=""
for p in $packages; do
	dpkg -s "$p" > /dev/null 2>&1 || missing="$missing $p"
done
if [ -n "$missing" ] || [ -z "$icu" ]; then
	say "Instalacija biblioteka:${missing:- (osvežavanje spiska paketa)}"
	sudo apt-get update
	[ -n "$icu" ] || icu=$(apt-cache search --names-only '^libicu[0-9]+$' | awk '{ print $1 }' | sort -V | tail -n 1)
	[ -n "$icu" ] || die "Paket libicuNN nije nađen u apt izvorima."
	# shellcheck disable=SC2086
	sudo DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends $missing $icu
fi

if ! id -nG "$USER" | tr ' ' '\n' | grep -qx dialout; then
	say "Dodavanje korisnika $USER u grupu dialout (serijski portovi)"
	sudo usermod -aG dialout "$USER"
fi

# ModemManager šalje AT komande serijskim uređajima (ometa POS i skener); zaustavlja se do sledećeg boot-a.
if systemctl is-active --quiet ModemManager 2> /dev/null; then
	say "Zaustavljanje ModemManager-a (do sledećeg boot-a)"
	sudo systemctl stop ModemManager
fi

# ─── 2. RustDesk (instalacija i podešavanje; prekinuto podešavanje se dovršava pri sledećem pokretanju) ──────────────
rustdesk_done="$root/.rustdesk-podesen"
if ! command -v rustdesk > /dev/null; then
	say "RustDesk: preuzimanje poslednjeg izdanja sa GitHub-a"
	[ "$(dpkg --print-architecture)" = amd64 ] || die "RustDesk: skripta podržava samo amd64."
	url=$(curl -fsSL https://api.github.com/repos/rustdesk/rustdesk/releases/latest | python3 -c '
import json, re, sys
assets = json.load(sys.stdin)["assets"]
deb = [a["browser_download_url"] for a in assets if re.fullmatch(r"rustdesk-[0-9.]+-x86_64\.deb", a["name"])]
print(deb[0] if deb else "")
')
	[ -n "$url" ] || die "RustDesk: .deb za x86_64 nije nađen u poslednjem izdanju."
	tmp=$(mktemp -d)
	curl -fL -o "$tmp/rustdesk.deb" "$url"
	sudo DEBIAN_FRONTEND=noninteractive apt-get install -y "$tmp/rustdesk.deb"
	rm -rf "$tmp"
fi
if ! systemctl is-active --quiet rustdesk; then
	sudo systemctl enable --now rustdesk
fi

if [ ! -f "$rustdesk_done" ]; then
	say "RustDesk: stalna lozinka"
	printf 'Lozinka (Enter = slučajna, bez kucanja na nepoznatoj tastaturi): '
	read -r -s password
	echo
	if [ -z "$password" ]; then
		# head zatvara cev pa tr dobija SIGPIPE; uz pipefail to ne sme da prekine skriptu.
		password=$(LC_ALL=C tr -dc 'A-HJ-NP-Za-km-z2-9' < /dev/urandom | head -c 12 || true)
	fi
	# Lozinka ide kroz standardni ulaz, ne kao argument sudo-a (sudo beleži argumente u sistemski dnevnik). Servisu
	# posle starta treba par sekundi; upis se ponavlja dok RustDesk ne potvrdi („Done“).
	ok=0
	for attempt in 1 2 3 4 5 6 7 8 9 10; do
		out=$(printf '%s\n' "$password" | sudo sh -c 'read -r p; rustdesk --password "$p"' 2>&1 || true)
		case "$out" in
			*Done*) ok=1; break ;;
		esac
		sleep 2
	done
	[ "$ok" -eq 1 ] || die "RustDesk nije prihvatio lozinku (servis ne odgovara). Pokrenuti skriptu ponovo."
	rd_id=""
	for attempt in 1 2 3 4 5 6 7 8 9 10; do
		rd_id=$(sudo rustdesk --get-id 2> /dev/null | tr -d '[:space:]' || true)
		[ -n "$rd_id" ] && break
		sleep 2
	done
	[ -n "$rd_id" ] || die "RustDesk ID nije dostupan (servis ne odgovara). Pokrenuti skriptu ponovo."

	# Rad bez potvrde na mašini traži X11 sesiju; GDM (GNOME) je podrazumevano na Wayland-u. Prijava se prebacuje na X11
	# samo ako X11 sesija i Xorg postoje — inače bi posle odjave ostala samo nedostupna sesija.
	x11_note=""
	if [ -f /etc/gdm3/daemon.conf ] && ! grep -q '^WaylandEnable=false' /etc/gdm3/daemon.conf; then
		if ls /usr/share/xsessions/*.desktop > /dev/null 2>&1 && { command -v Xorg > /dev/null || [ -x /usr/lib/xorg/Xorg ]; }; then
			if grep -q '^#*WaylandEnable=' /etc/gdm3/daemon.conf; then
				sudo sed -i 's/^#*WaylandEnable=.*/WaylandEnable=false/' /etc/gdm3/daemon.conf
			else
				sudo sed -i 's/^\[daemon\]$/[daemon]\nWaylandEnable=false/' /etc/gdm3/daemon.conf
			fi
			x11_note="GNOME prijava je prebačena na X11 — važi od sledeće prijave (odjava ili restart)."
		else
			x11_note="X11 sesija nije instalirana — prijava ostaje Wayland, pa RustDesk veza traži potvrdu na mašini."
		fi
	elif [ "${XDG_SESSION_TYPE:-}" = wayland ] && [ ! -f /etc/gdm3/daemon.conf ]; then
		x11_note="Sesija je Wayland: pri sledećoj prijavi izabrati X11 sesiju (npr. „Plasma (X11)“), inače veza traži potvrdu."
	fi

	# Postojeća instalacija može tražiti klik na mašini ili samo privremenu lozinku — tada stalna lozinka ne pomaže.
	rd_conf=$(sudo cat /root/.config/rustdesk/RustDesk2.toml 2> /dev/null || true)
	if printf '%s' "$rd_conf" | grep -Eq "approve-mode *= *['\"]click['\"]|verification-method *= *['\"]use-temporary-password['\"]"; then
		x11_note="$x11_note PAŽNJA: RustDesk je podešen da traži klik ili privremenu lozinku (Podešavanja → Bezbednost: prihvatanje lozinkom, stalna lozinka)."
	fi

	say "RustDesk ID: $rd_id   LOZINKA: $password"
	echo "    SLIKATI ili zapisati — lozinka se ne čuva nigde drugde."
	[ -n "$x11_note" ] && echo "    $x11_note"
	printf '\nEnter kad su ID i lozinka zapisani...'
	read -r _
	# Tek posle potvrde: prekid pre toga ponavlja podešavanje (i prikaz) pri sledećem pokretanju.
	mkdir -p "$root"
	touch "$rustdesk_done"
fi

# ─── 3. Program i konfiguracija ──────────────────────────────────────────────────────────────────────────────────────
name=$(basename "$archive" .tar.gz)
app="$root/$name/app"
if [ ! -f "$root/$name/.raspakovan" ]; then
	say "Raspakivanje programa u $root"
	mkdir -p "$root"
	rm -rf "$root/.raspakivanje"
	mkdir "$root/.raspakivanje"
	tar -xzf "$archive" -C "$root/.raspakivanje" || { rm -rf "$root/.raspakivanje"; die "Paket nije ispravan (prekinut prenos?)."; }
	[ -f "$root/.raspakivanje/$name/app/TopPos.Kiosk" ] || { rm -rf "$root/.raspakivanje"; die "U paketu nema programa."; }
	chmod u+x "$root/.raspakivanje/$name/app/TopPos.Kiosk"
	touch "$root/.raspakivanje/$name/.raspakovan"
	# Isto izdanje raspakovano ručno (bez markera) ima svoju konfiguraciju, logove i stanje u app/ — sklanja se, ne briše.
	# Redovi potvrda (neposlate potvrde naplate i preuzimanja) prelaze u stalni folder stanja, da ih program pošalje.
	if [ -e "$root/$name" ]; then
		mkdir -p "$state_dir"
		# Prazan red je „[]“ (program ga tako upiše kad pošalje sve); takav se ne prenosi i ne smeta.
		pending() {
			[ -f "$1" ] && python3 -c 'import json, sys; sys.exit(0 if json.load(open(sys.argv[1], encoding="utf-8-sig")) else 1)' "$1" 2> /dev/null
		}
		# Prvo provera oba reda, pa tek onda premeštanje — sukob ne ostavlja delimično prenete redove.
		for queue in PaymentConfirmation.json ServerConfirmations.json; do
			if pending "$root/$name/app/$queue" && pending "$state_dir/$queue"; then
				rm -rf "$root/.raspakivanje"
				die "Neposlate potvrde postoje i u ručnoj probi ($root/$name/app/$queue) i u $state_dir/$queue. Pokrenuti ručnu probu dok server ne primi potvrde (fajl tada sadrži []), ugasiti je, pa ponovo ovu skriptu."
			fi
		done
		for queue in PaymentConfirmation.json ServerConfirmations.json; do
			if pending "$root/$name/app/$queue"; then
				mv "$root/$name/app/$queue" "$state_dir/$queue"
				echo "Neposlate potvrde $queue iz ručne probe prenete u $state_dir."
			fi
		done
		kept="$root/$name-rucno-$(date +%Y%m%d-%H%M%S)"
		mv "$root/$name" "$kept"
		echo "Ranije raspakovano izdanje (ručna proba) sačuvano kao $kept."
	fi
	mv "$root/.raspakivanje/$name" "$root/$name"
	rm -rf "$root/.raspakivanje"
fi

# copy_new <izvor> <cilj>: preko privremenog fajla, da prekid (izvađen USB, pun disk) ne ostavi nepotpun fajl pod
# konačnim imenom.
copy_new() {
	cp "$1" "$2.novo" && mv "$2.novo" "$2" || { rm -f "$2.novo"; die "Kopiranje $1 nije uspelo."; }
}
mkdir -p "$config_dir" "$state_dir" "$log_dir"
chmod 700 "$config_dir"
[ -f "$config_dir/serilog.json" ] || copy_new "$root/$name/serilog.json" "$config_dir/serilog.json"
if [ ! -f "$config_dir/app-configuration.json" ]; then
	# Neispravan JSON sa USB-a ne dobija konačno ime, pa je posle ispravke izvora dovoljno ponovo pokrenuti skriptu.
	cp "$kit/app-configuration.json" "$config_dir/app-configuration.json.novo" || die "Kopiranje konfiguracije nije uspelo."
	if ! python3 -c 'import json, sys; json.load(open(sys.argv[1], encoding="utf-8-sig"))' "$config_dir/app-configuration.json.novo" 2> /dev/null; then
		rm -f "$config_dir/app-configuration.json.novo"
		die "$kit/app-configuration.json nije ispravan JSON — ispraviti ga i pokrenuti ponovo."
	fi
	mv "$config_dir/app-configuration.json.novo" "$config_dir/app-configuration.json"
fi
# U konfiguraciji su lozinke servisa i ključevi POS-a.
chmod 600 "$config_dir/app-configuration.json"

# ─── 4. Serijski portovi ─────────────────────────────────────────────────────────────────────────────────────────────
ports=()
covered=" "
for p in /dev/serial/by-id/*; do
	# Adapter izvučen tokom popisa: link (i prazan by-id folder) nestaje, pa se port preskače umesto prekida (set -e).
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
# Portovi na ploči (ttyS) koji stvarno postoje (UART poznat kernelu), sa adresom i IRQ-om za poređenje sa BIOS-om, gde
# su obično označeni kao COM1..COM4. Red izgleda ovako: "1: uart:16550A port:000002F8 irq:3 tx:0 rx:0".
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
done < <(sudo cat /proc/tty/driver/serial 2> /dev/null | grep -E '^[0-9]+:' | grep -v 'uart:unknown' || true)

config="$config_dir/app-configuration.json"
current() {
	python3 - "$config" "$1" << 'PY'
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
	now=$(current "$key") || die "Konfiguracija $config nije ispravan JSON."
	nows+=("$now")
done

say "Serijski portovi"
if [ ${#ports[@]} -eq 0 ]; then
	echo "Nijedan serijski port nije nađen — priključiti uređaje i pokrenuti ponovo."
fi
for i in "${!ports[@]}"; do
	p=${ports[$i]}
	# -m: USB adapter izvučen posle pravljenja spiska ne prekida skriptu (set -e).
	target=$(canon "$p")
	line=$(printf '  %d) %s' $((i + 1)) "$p")
	[ "$target" != "$p" ] && line="$line -> $target"
	[ -n "${details[$p]:-}" ] && line="$line  ${details[$p]}"
	for j in "${!keys[@]}"; do
		[ "$(canon "${nows[$j]}")" = "$target" ] && line="$line  [sada: ${labels[$j]}]"
	done
	echo "$line"
done

# Jedan port može imati samo jedan uređaj (dva uređaja na istom portu se međusobno ometaju). Poređenje i po putanji i po
# stvarnom uređaju, razrešeno pri svakom izboru: adapter izvučen između dva izbora ne sakriva sukob.
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
			# Ista putanja, isti uređaj u trenutku ranijeg izbora ili isti uređaj sada.
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

# Upis preko privremenog fajla: prekid ili pun disk ne ostavljaju oštećenu konfiguraciju.
python3 - "$config" "${choices[@]}" << 'PY'
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
# Proba POS-a: bez TerminalInstalled program POS ne koristi ni ne proverava. POS bez porta (izbor 0) je isključen, pa
# proba vrata i skenera radi i bez podešavanja POS-a (npr. valute).
if not isinstance(c.get("PosTerminal"), dict):
	c["PosTerminal"] = {}
pos_port = (c["PosTerminal"].get("ComPort") or {}).get("PortName")
c["PosTerminal"]["TerminalInstalled"] = bool(pos_port)
# mkstemp pravi fajl sa pravima 0600 (u konfiguraciji su lozinke), u istom folderu da os.replace bude atomski.
fd, tmp = tempfile.mkstemp(dir=os.path.dirname(path), prefix=".app-configuration-")
with os.fdopen(fd, "w", encoding="utf-8") as f:
	json.dump(c, f, indent=2, ensure_ascii=False)
	f.flush()
	os.fsync(f.fileno())
os.replace(tmp, path)
PY

# ─── 5. Pokretanje ───────────────────────────────────────────────────────────────────────────────────────────────────
say "Pokretanje programa (gašenje sa Start stranice: Ctrl+C ovde ili Alt+F4). Log: $log_dir"
# Isti folderi kao pod systemd-om (LinuxAppPaths): konfiguracija, stanje i logovi van foldera izdanja.
export CONFIGURATION_DIRECTORY="$config_dir" STATE_DIRECTORY="$state_dir" LOGS_DIRECTORY="$log_dir"
cd "$app"
if id -nG | tr ' ' '\n' | grep -qx dialout; then
	exec ./TopPos.Kiosk
fi
# Grupa dialout je upravo dodata i ova sesija je još nema: sg je daje samo programu, bez odjave.
exec sg dialout -c ./TopPos.Kiosk
