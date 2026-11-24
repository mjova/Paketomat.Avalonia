#!/bin/sh
# Simulatori servera (http://localhost:18080) i kontrolera vrata NCU24L (127.0.0.1:14001) u ovom terminalu; paketi za
# probu se dodaju čim server proradi (dodaj-pakete.sh). Gašenje: Ctrl+C. Log: simulatori/Logs.
#   ./pokreni-simulatore.sh
set -eu
cd "$(dirname "$0")"

# Druga instanca bi pala na zauzetom portu, a paketi bi otišli u staru.
if curl -fsS http://localhost:18080/_sim/health > /dev/null 2>&1; then
	echo "Simulatori već rade (port 18080 zauzet) — koristiti postojeći terminal ili ga ugasiti (Ctrl+C)." >&2
	exit 1
fi
# Program koji radi mogao bi da pošalje neposlatu potvrdu naplate pre nego što se paketi dodaju; simulator je tada odbija
# (nepoznata šifra) i program je briše.
if pgrep -x TopPos.Kiosk > /dev/null; then
	echo "Program kioska radi — prvo ga ugasiti (sa Start stranice), pa pokrenuti simulatore, pa program." >&2
	exit 1
fi

# Raspakuje se kad paket nije raspakovan ili je stigao drugi paket (SHA-256). Nov paket se raspakuje pored i proverava,
# pa tek onda zamenjuje postojeći (koji ostaje kao simulatori-<vreme>).
count=$(ls toppos-simulators-*.tar.gz 2> /dev/null | wc -l)
if [ "$count" -ne 1 ]; then
	echo "Pored skripte mora biti tačno jedan paket toppos-simulators-*.tar.gz (ima $count)." >&2
	exit 1
fi
archive=$(ls toppos-simulators-*.tar.gz)
sum=$(sha256sum "$archive" | cut -d ' ' -f 1)
if [ "$(cat simulatori/.paket 2> /dev/null)" != "$sum" ]; then
	rm -rf simulatori.novi
	mkdir simulatori.novi
	if ! tar -xzf "$archive" -C simulatori.novi --strip-components=1 || [ ! -f simulatori.novi/TopPos.Simulators ]; then
		rm -rf simulatori.novi
		echo "Paket $archive nije ispravan (prekinut prenos?) — postojeći simulatori nisu dirani." >&2
		exit 1
	fi
	chmod 755 simulatori.novi/TopPos.Simulators
	echo "$sum" > simulatori.novi/.paket
	if [ -d simulatori ]; then
		mv simulatori "simulatori-$(date +%Y%m%d-%H%M%S)"
	fi
	mv simulatori.novi simulatori
	echo "Raspakovan $archive."
fi
cp server.json locker.json simulatori/

sh ./dodaj-pakete.sh &
cd simulatori
exec ./TopPos.Simulators both --server-config server.json --locker-config locker.json
