#!/usr/bin/env bash
# Paket kioska za Linux (doc/deploy-linux.md): Release, self-contained, bez trimovanja (Avalonia i Serilog konfiguracija
# koriste refleksiju). Ishod: artifacts/toppos-kiosk-<verzija>-<rid>.tar.gz sa programom i fajlovima za instalaciju.
# Konfiguracija mašine (app-configuration.json) ne ulazi u paket — daje se instalaciji (install.sh --config).
# Upotreba: deploy/linux/publish.sh [--rid linux-x64|linux-arm64] [--version 4.0.0]
# Radi i iz Git Bash-a na Windows-u (prava izvršavanja postavlja install.sh).
set -euo pipefail
cd "$(dirname "$0")/../.."

rid=linux-x64
version=
while [ $# -gt 0 ]; do
	case "$1" in
		--rid) rid="$2"; shift 2 ;;
		--version) version="$2"; shift 2 ;;
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

# Oznaka izdanja je i ime foldera na uređaju (install.sh ne prepisuje instalirano izdanje), pa mora biti jedinstvena:
# verzija ili „dev“ + vreme build-a + commit (+ „-dirty“ uz neupisane izmene).
hash=$(git rev-parse --short=12 HEAD)
dirty=
if [ -n "$(git status --porcelain)" ]; then
	dirty=-dirty
fi
label="${version:-dev}-$(date -u +%Y%m%d%H%M%S)-$hash$dirty"

name="toppos-kiosk-$label-$rid"
stage="artifacts/$name"
rm -rf "$stage" "artifacts/$name.tar.gz"
mkdir -p "$stage"

dotnet publish src/TopPos.Kiosk.Avalonia/TopPos.Kiosk.Avalonia.csproj -c Release -r "$rid" --self-contained \
	-p:PublishTrimmed=false -o "$stage/app" --disable-build-servers

# Ne sme u paket: podešavanje mašine i razvojni fajlovi (Content uslovi ih u Release-u ne kopiraju, ovo je osigurač).
rm -f "$stage"/app/app-configuration*.json
# serilog.json ide u /etc/toppos-kiosk (ConfigurationDirectory); install.sh ga kopira samo ako ga tamo nema.
mv "$stage/app/serilog.json" "$stage/serilog.json"
# Natpis verzije u logu i zaglavlju (KioskHost čita paketomat.ver uz program), kao v2.
printf '%s\n' "$label" > "$stage/app/paketomat.ver"

cp deploy/linux/install.sh deploy/linux/boot-mode.sh deploy/linux/toppos-kiosk.service deploy/linux/toppos-kiosk.pam "$stage/"
cp deploy/linux/kiosk-session.sh "$stage/app/"
# Prava izvršavanja ne zavise od checkout-a (Windows ih ne čuva); install.sh ih ionako postavlja na uređaju.
chmod 755 "$stage/install.sh" "$stage/boot-mode.sh" "$stage/app/kiosk-session.sh" "$stage/app/TopPos.Kiosk"

tar -C artifacts -czf "artifacts/$name.tar.gz" "$name"
echo "Paket: artifacts/$name.tar.gz"
