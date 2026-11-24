#!/bin/sh
# Spisak serijskih portova: stalna imena (/dev/serial/by-id, ne menjaju se posle restarta ili prebacivanja u drugi USB)
# i na šta pokazuju. Koji je koji uređaj: pokrenuti, izvaditi jedan uređaj, pokrenuti ponovo — nestali red je taj uređaj.
echo "Stalna imena (za app-configuration.json):"
if [ -d /dev/serial/by-id ]; then
	for p in /dev/serial/by-id/*; do
		echo "  $p -> $(readlink -f "$p")"
	done
else
	echo "  (nema — nijedan USB serijski uređaj nije priključen)"
fi
echo
echo "Svi serijski uređaji:"
ls -l /dev/ttyACM* /dev/ttyUSB* 2> /dev/null || echo "  (nema /dev/ttyACM* ni /dev/ttyUSB*)"
echo
if id -nG | tr ' ' '\n' | grep -qx dialout; then
	echo "Grupa dialout: da."
else
	echo "Grupa dialout: NE — pokrenuti sudo ./priprema.sh, pa se odjaviti i prijaviti." >&2
fi
