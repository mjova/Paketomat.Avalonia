#!/usr/bin/env bash
# Komplet za probu programa na Debian mašini sa pravim POS-om i skenerom, bez kiosk režima (deploy/linux/proba/UPUTSTVO.md):
# paket kioska, simulatori servera i kontrolera vrata, konfiguracija, skripte za pokretanje i prikupljanje logova.
# Ishod: Installer/debian/ (van git-a) — ceo folder se nosi na mašinu (USB).
# Upotreba: deploy/linux/pripremi-probu.sh [--rid linux-x64|linux-arm64]
# Radi i iz Git Bash-a na Windows-u.
set -euo pipefail
cd "$(dirname "$0")/../.."

rid=linux-x64
while [ $# -gt 0 ]; do
	case "$1" in
		--rid) rid="$2"; shift 2 ;;
		*) echo "Nepoznat argument: $1" >&2; exit 2 ;;
	esac
done

if ! command -v dotnet > /dev/null; then
	if [ -x "$HOME/.dotnet/dotnet" ]; then
		export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"
	else
		echo ".NET SDK (dotnet) nije na PATH-u ni u ~/.dotnet." >&2
		exit 1
	fi
fi

# Nov komplet se pravi pored postojećeg; postojeći (možda sa ručno popunjenim kiosk-proba.json) se sklanja kao
# Installer/debian-<vreme> tek kad je novi gotov.
final=Installer/debian
mkdir -p Installer
out=$(mktemp -d Installer/.debian-XXXXXX)
trap 'rm -rf "$out"' EXIT

# Kiosk: isti paket kao za isporuku.
deploy/linux/publish.sh --rid "$rid" | tee "$out/.publish.log"
kiosk=$(sed -n 's/^Paket: //p' "$out/.publish.log" | tail -n 1)
rm -f "$out/.publish.log"
cp "$kiosk" "$out/"

# Simulatori: self-contained, bez trimovanja; serilog.json simulatora piše u Logs/ radnog foldera servisa.
sim=toppos-simulators-$(git rev-parse --short=12 HEAD)-$rid
rm -rf "artifacts/$sim" "artifacts/$sim.tar.gz"
dotnet publish tools/TopPos.Simulators/TopPos.Simulators.csproj -c Release -r "$rid" --self-contained \
	-p:PublishTrimmed=false -o "artifacts/$sim" --disable-build-servers
tar -C artifacts -czf "$out/$sim.tar.gz" "$sim"

cp deploy/linux/proba/UPUTSTVO.md deploy/linux/proba/kiosk-proba.json deploy/linux/proba/server.json \
	deploy/linux/proba/locker.json "$out/"
cp deploy/linux/proba/*.sh "$out/"
chmod 755 "$out"/*.sh

if [ -d "$final" ]; then
	previous="$final-$(date +%Y%m%d-%H%M%S)"
	mv "$final" "$previous"
	echo "Prethodni komplet: $previous"
fi
mv "$out" "$final"

echo
echo "Komplet za probu: $final"
ls -l "$final"
