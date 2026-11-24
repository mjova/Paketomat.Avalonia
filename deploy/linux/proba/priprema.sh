#!/usr/bin/env bash
# Jednom, pre prvog pokretanja (UPUTSTVO.md, korak 2): sistemske biblioteke, pristup serijskim portovima, ModemManager.
#   sudo ./priprema.sh
# Traži internet (apt). Posle nje se treba odjaviti i ponovo prijaviti (grupa dialout važi tek u novoj sesiji).
set -euo pipefail

if [ "$(id -u)" -ne 0 ]; then
	echo "Pokrenuti kao root (sudo ./priprema.sh)." >&2
	exit 1
fi
user="${SUDO_USER:-}"
if [ -z "$user" ] || [ "$user" = root ]; then
	echo "Pokrenuti sa sudo iz naloga koji će pokretati program (ne kao root)." >&2
	exit 1
fi

# ICU (globalizacija .NET-a; ime paketa zavisi od izdanja), LibVLC (zvuk), fontconfig (Skia), curl (paketi u simulatoru),
# xwayland (Avalonia crta kroz X11; na Wayland desktopu preko XWayland-a) i X11 biblioteke koje Avalonia.X11 učitava
# (libICE/libSM nema na sistemu bez punog desktopa — start pada sa DllNotFoundException).
apt-get update
icu=$(apt-cache search --names-only '^libicu[0-9]+$' | awk '{ print $1 }' | sort -V | tail -n 1)
if [ -z "$icu" ]; then
	echo "Paket libicuNN nije nađen u apt izvorima." >&2
	exit 1
fi
DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends "$icu" libvlc5 vlc-plugin-base libfontconfig1 curl python3 xwayland \
	libx11-6 libice6 libsm6 libxcursor1 libxext6 libxfixes3 libxi6 libxrandr2 libgl1

# Serijski portovi (POS, skener): grupa dialout.
usermod --append --groups dialout "$user"

# ModemManager pri priključenju šalje AT komande na /dev/ttyACM* i /dev/ttyUSB* (misli da je modem) — to ometa POS i
# skener. Za probu se gasi; vraćanje: sudo systemctl enable --now ModemManager
if systemctl list-unit-files ModemManager.service > /dev/null 2>&1 && systemctl is-enabled --quiet ModemManager.service 2> /dev/null; then
	systemctl disable --now ModemManager.service
	echo "ModemManager isključen (vraćanje: sudo systemctl enable --now ModemManager)."
fi
# brltty (Ubuntu) preuzima USB-serijske adaptere (CH340/CP210x) kao Brajev red.
if dpkg -s brltty > /dev/null 2>&1; then
	echo "UPOZORENJE: instaliran je brltty — može da preuzme USB-serijski adapter. Ako port nestaje: sudo apt remove brltty" >&2
fi

echo
echo "Gotovo. Odjaviti se i ponovo prijaviti (grupa dialout), pa ./serijski-portovi.sh."
