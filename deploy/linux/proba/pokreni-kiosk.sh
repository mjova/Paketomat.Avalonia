#!/bin/sh
# Program kioska u desktop sesiji (bez kiosk režima i bez instalacije). Program je u kiosk/ (konfiguracija se pri svakom
# pokretanju kopira iz kiosk-proba.json pored ove skripte); stanje (status.lock, redovi potvrda) je u stanje/, a log u
# logovi-kiosk/ — van programa, pa nov paket ne odvaja neposlate potvrde ni logove.
# Program zauzima ceo ekran. Gašenje sa Start stranice: Ctrl+C u ovom terminalu (Alt+Tab do njega) ili Alt+F4.
#   ./pokreni-kiosk.sh
set -eu
cd "$(dirname "$0")"
here=$(pwd)

# Avalonia na Linuxu crta kroz X11: na Wayland desktopu preko XWayland-a, koji postavlja DISPLAY.
if [ -z "${DISPLAY:-}" ]; then
	echo "Nema DISPLAY: pokrenuti iz terminala na desktopu. Na Wayland desktopu treba XWayland (sudo ./priprema.sh)" >&2
	echo "ili izabrati X11 sesiju pri prijavi." >&2
	exit 1
fi

# Raspakuje se kad program nije raspakovan ili je stigao drugi paket (SHA-256). Nov paket se raspakuje pored i proverava,
# pa tek onda zamenjuje postojeći program (koji ostaje kao kiosk-<vreme>).
count=$(ls toppos-kiosk-*.tar.gz 2> /dev/null | wc -l)
if [ "$count" -ne 1 ]; then
	echo "Pored skripte mora biti tačno jedan paket toppos-kiosk-*.tar.gz (ima $count)." >&2
	exit 1
fi
archive=$(ls toppos-kiosk-*.tar.gz)
sum=$(sha256sum "$archive" | cut -d ' ' -f 1)
if [ "$(cat kiosk/.paket 2> /dev/null)" != "$sum" ]; then
	rm -rf kiosk.novi kiosk.tar
	mkdir kiosk.tar kiosk.novi
	if ! tar -xzf "$archive" -C kiosk.tar || ! cp -a kiosk.tar/*/app/. kiosk.novi/ || ! cp kiosk.tar/*/serilog.json kiosk.novi/ \
		|| [ ! -f kiosk.novi/TopPos.Kiosk ]; then
		rm -rf kiosk.novi kiosk.tar
		echo "Paket $archive nije ispravan (prekinut prenos ili pun disk?) — postojeći program nije diran." >&2
		exit 1
	fi
	rm -rf kiosk.tar
	chmod 755 kiosk.novi/TopPos.Kiosk
	echo "$sum" > kiosk.novi/.paket
	if [ -d kiosk ]; then
		mv kiosk "kiosk-$(date +%Y%m%d-%H%M%S)"
	fi
	mv kiosk.novi kiosk
	echo "Raspakovan $archive."
fi
cp kiosk-proba.json kiosk/app-configuration.json
mkdir -p stanje logovi-kiosk

if ! id -nG | tr ' ' '\n' | grep -qx dialout; then
	echo "UPOZORENJE: korisnik nije u grupi dialout — POS i skener se neće otvoriti (sudo ./priprema.sh, pa odjava i prijava)." >&2
fi
# Simulator mora da radi i da ima sve pakete pre starta: program odmah šalje neposlate potvrde naplate, a simulator bez
# paketa ih odbija (nepoznata šifra) i program ih briše.
if ! sh ./dodaj-pakete.sh --provera; then
	echo "Simulatori ne rade ili paketi još nisu dodati — prvo ./pokreni-simulatore.sh u drugom terminalu i sačekati" >&2
	echo "red „>>> Paketi za probu“." >&2
	exit 1
fi

# Isti folderi kao pod systemd-om (LinuxAppPaths); konfiguracija ostaje uz program.
export STATE_DIRECTORY="$here/stanje" LOGS_DIRECTORY="$here/logovi-kiosk"
cd kiosk
exec ./TopPos.Kiosk
