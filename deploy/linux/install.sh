#!/usr/bin/env bash
# Instalacija kioska iz paketa (publish.sh) na Debian 12 / Ubuntu 24.04 — pokreće root iz raspakovanog paketa.
#   sudo ./install.sh [--config <app-configuration.json>] [--kiosk-boot]
# Bez --kiosk-boot: program, servis, korisnik i podešavanja su na mestu, ali se pri boot-u i dalje diže postojeći desktop;
# servis se pokreće ručno (systemctl start toppos-kiosk). Sa --kiosk-boot: boot-mode.sh kiosk (multi-user.target sa
# servisom, desktop se ne diže). Povratak: sudo /opt/toppos-kiosk/boot-mode.sh desktop (doc/deploy-linux.md).
set -euo pipefail
cd "$(dirname "$0")"

config=
kiosk_boot=0
while [ $# -gt 0 ]; do
	case "$1" in
		--config) config="$2"; shift 2 ;;
		--kiosk-boot) kiosk_boot=1; shift ;;
		*) echo "Nepoznat argument: $1" >&2; exit 2 ;;
	esac
done

if [ "$(id -u)" -ne 0 ]; then
	echo "Pokrenuti kao root (sudo)." >&2
	exit 1
fi
if [ ! -x "$(command -v apt-get)" ]; then
	echo "Podržani su Debian i Ubuntu (apt-get)." >&2
	exit 1
fi
if [ -n "$config" ] && [ ! -f "$config" ]; then
	echo "Nema fajla $config." >&2
	exit 1
fi

label=$(cat app/paketomat.ver)
prefix=/opt/toppos-kiosk
release="$prefix/releases/$label"
etc=/etc/toppos-kiosk

# Instalirano izdanje se ne prepisuje (servis ga možda koristi); provera pre bilo kakve izmene sistema.
if [ -e "$release" ]; then
	echo "Izdanje $label je već instalirano ($release). Novi paket ima novu oznaku (publish.sh)." >&2
	exit 1
fi

# Zavisnosti: cage + XWayland (Avalonia na Linuxu crta kroz X11), ICU (globalizacija .NET-a), LibVLC (zvuk; dinamički,
# LGPL-2.1), fontconfig (Skia). Ime ICU paketa zavisi od izdanja (libicu72 na Debianu 12, libicu74 na Ubuntu 24.04).
apt-get update
icu=$(apt-cache search --names-only '^libicu[0-9]+$' | awk '{ print $1 }' | sort -V | tail -n 1)
if [ -z "$icu" ]; then
	echo "Paket libicuNN nije nađen u apt izvorima." >&2
	exit 1
fi
DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
	cage xwayland "$icu" libvlc5 vlc-plugin-base libfontconfig1 dbus-user-session

# Korisnik programa: dialout za serijske uređaje (kontroler vrata, skener, POS). Ekran, touch i zvuk daje logind sesija.
if ! id toppos > /dev/null 2>&1; then
	useradd --system --create-home --home-dir /home/toppos --shell /usr/sbin/nologin --groups dialout toppos
else
	usermod --append --groups dialout toppos
fi

# Program: svako izdanje u svom folderu, „current“ pokazuje na aktivno (vraćanje = promena linka). Novo izdanje se kopira
# pored pa preimenuje, a link se menja atomski — prekid instalacije ne ostavlja „current“ na nepotpunom izdanju.
mkdir -p "$prefix/releases"
staging=$(mktemp -d "$prefix/releases/.staging-XXXXXX")
trap 'rm -rf "$staging"' EXIT
cp -a app/. "$staging/"
chmod 755 "$staging" "$staging/TopPos.Kiosk" "$staging/kiosk-session.sh"
chown -R root:root "$staging"
mv -T "$staging" "$release"
trap - EXIT
ln -sfn "$release" "$prefix/.current-new"
mv -T "$prefix/.current-new" "$prefix/current"
install -m 755 boot-mode.sh "$prefix/boot-mode.sh"

# Konfiguracija: postojeći fajlovi se ne diraju (podešavanja mašine); app-configuration.json samo uz --config.
mkdir -p "$etc"
if [ ! -f "$etc/serilog.json" ]; then
	install -m 644 serilog.json "$etc/serilog.json"
fi
if [ -n "$config" ]; then
	install -m 644 "$config" "$etc/app-configuration.json"
fi
if [ ! -f "$etc/app-configuration.json" ]; then
	echo "UPOZORENJE: nema $etc/app-configuration.json — kiosk neće startovati dok se ne doda (--config)." >&2
fi

install -m 644 toppos-kiosk.service /etc/systemd/system/toppos-kiosk.service
install -m 644 toppos-kiosk.pam /etc/pam.d/toppos-kiosk

# Uređaj ne sme da zaspi (Debian VM: KDE je posle 15 min bez unosa uspavao gosta usred rada kioska).
systemctl mask sleep.target suspend.target hibernate.target hybrid-sleep.target
systemctl daemon-reload

if [ "$kiosk_boot" -eq 1 ]; then
	"$prefix/boot-mode.sh" kiosk
	echo "Restart: systemctl reboot. Povratak na desktop: sudo $prefix/boot-mode.sh desktop."
else
	echo "Instalirano. Ručno pokretanje: systemctl start toppos-kiosk (prebacuje na tty1)."
fi
echo "Izdanje $label u $release."
