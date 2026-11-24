#!/bin/sh
# Simulator kontrolera ostavlja otvorena vrata otvorenim, kao pravi paketomat dok kupac ne zatvori vrata. Kiosk posle
# otvaranja čeka da se vrata zatvore; bez toga posle isteka javlja grešku vrata. Ovo „zatvara“ vrata u simulatoru.
#   ./zatvori-vrata.sh 5        (jedna vrata)
#   ./zatvori-vrata.sh          (sva vrata)
url=http://localhost:18299/_sim/doors
if [ -n "${1:-}" ]; then
	curl -fsS -d '' "$url/$1/close" > /dev/null && echo "Vrata $1 zatvorena."
else
	curl -fsS -d '' "$url/close-all" > /dev/null && echo "Sva vrata zatvorena."
fi
