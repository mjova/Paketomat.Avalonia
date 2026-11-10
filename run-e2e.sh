#!/usr/bin/env bash
# E2E testovi (Linux): pravi kiosk nad simulatorima servera i paketomata, UI preko AT-SPI (doc/e2e.md).
# Preduslovi: grafička sesija (X11 ili Wayland) sa D-Bus sesijom, zatvorena razvojna instanca kioska, ~2 GB slobodno u
# /tmp (sandbox testa ~0,6 GB; prošao test ga briše). Serijski uređaji idu preko pty parova simulatora (bez com0com-a).
# Testovi sa pravim mišem (RealMouse*) se na Linuxu preskaču.
# Dodatni argumenti idu u dotnet test, npr.: ./run-e2e.sh --filter "FullyQualifiedName~RecipientPickupTests"
set -euo pipefail
cd "$(dirname "$0")"

# .NET SDK: sa PATH-a, inače iz ~/.dotnet (instalacija skriptom dotnet-install)
if ! command -v dotnet > /dev/null; then
	if [ -x "$HOME/.dotnet/dotnet" ]; then
		export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"
	else
		echo ".NET SDK (dotnet) nije na PATH-u ni u ~/.dotnet." >&2
		exit 1
	fi
fi

if [ -z "${WAYLAND_DISPLAY:-}" ] && [ -z "${DISPLAY:-}" ]; then
	echo "Nema grafičke sesije (DISPLAY/WAYLAND_DISPLAY) — pokrenuti iz terminala u desktop sesiji." >&2
	exit 1
fi
if [ -z "${DBUS_SESSION_BUS_ADDRESS:-}" ]; then
	echo "Nema D-Bus sesije (DBUS_SESSION_BUS_ADDRESS) — AT-SPI ne može da se koristi." >&2
	exit 1
fi
free_mb=$(df -Pm "${TMPDIR:-/tmp}" | awk 'NR == 2 { print $4 }')
if [ "$free_mb" -lt 2048 ]; then
	echo "U ${TMPDIR:-/tmp} je slobodno samo ${free_mb} MB (sandbox testa je ~0,6 GB) — osloboditi prostor (npr. stari /tmp/toppos-e2e/*)." >&2
	exit 1
fi

# Prvo testovi jezgra kioska (bez UI-ja i hardvera): ako padnu, E2E se ne pokreće. Dodatni argumenti idu samo u E2E.
if ! dotnet test tests/TopPos.Kiosk.Tests/TopPos.Kiosk.Tests.csproj -c Debug --disable-build-servers --logger "console;verbosity=minimal"; then
	echo "Testovi jezgra nisu prošli, E2E se ne pokreće." >&2
	exit 1
fi

# Build celog rešenja: E2E pokreće izlaze kioska i simulatora iz njihovih bin foldera. Bez build servera: posle skripte
# ne ostaju MSBuild procesi.
dotnet build Paketomat.slnx -c Debug --disable-build-servers

# Izveštaj (HTML za browser, TRX za alate) ide u TestResults/E2E pored ove skripte.
stamp=$(date +%Y%m%d-%H%M%S)
TOPPOS_E2E=1 exec dotnet test tests/TopPos.Kiosk.E2E.Tests/TopPos.Kiosk.E2E.Tests.csproj -c Debug --no-build --disable-build-servers \
	--logger "console;verbosity=normal" --logger "html;LogFileName=e2e-$stamp.html" --logger "trx;LogFileName=e2e-$stamp.trx" \
	--results-directory "$PWD/TestResults/E2E" "$@"
