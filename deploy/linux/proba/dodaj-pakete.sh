#!/bin/sh
# Paketi za probu u simulatoru servera. Simulator ih drži u memoriji, pa ih pokreni-simulatore.sh dodaje pri svakom
# startu; ručno (simulator radi): ./dodaj-pakete.sh
#   šifra 123456 → vrata 5, bez plaćanja
#   šifra 234567 → vrata 7, bez plaćanja
#   šifra 345678 → vrata 9, otkup 10 RSD — PRAVI POS terminal naplaćuje karticu (iznos se menja ovde, polje iznosOtkupa)
# Telefon primaoca nije zadat, pa se prihvata bilo koji broj.
# ./dodaj-pakete.sh --provera: ništa ne dodaje, samo proverava da simulator ima sve pakete sa ovim vrednostima (izlaz 0/1);
# koristi je pokreni-kiosk.sh pre starta programa.
url=http://localhost:18080/_sim
packages="PROBA-001 5 123456 0 M
PROBA-002 7 234567 0 S
PROBA-003 9 345678 10 L"

# matches <barkod> <vrata> <šifra> <iznos>: postoji paket sa tim barkodom i tačno tim vratima, šifrom i iznosom.
matches() {
	curl -fsS "$url/packages" 2> /dev/null | python3 -c '
import json, sys
from decimal import Decimal
b, door, code, amount = sys.argv[1], int(sys.argv[2]), int(sys.argv[3]), Decimal(sys.argv[4])
p = [x for x in json.load(sys.stdin, parse_float=Decimal) if x["barkod"] == b]
sys.exit(0 if p and p[0]["brojPozicije"] == door and p[0]["kodZaOtvaranje"] == code and Decimal(p[0]["iznosOtkupa"]) == amount else 1)
' "$1" "$2" "$3" "$4" 2> /dev/null
}

if [ "${1:-}" = "--provera" ]; then
	echo "$packages" | while read -r barcode door code amount size; do
		matches "$barcode" "$door" "$code" "$amount" || exit 1
	done
	exit $?
fi

# Simulator posle starta treba par sekundi da otvori port.
i=0
until curl -fsS "$url/health" > /dev/null 2>&1; do
	i=$((i + 1))
	if [ "$i" -ge 120 ]; then
		echo "Simulator servera ne odgovara na $url/health — paketi nisu dodati." >&2
		exit 1
	fi
	sleep 0.5
done

# add <barkod> <vrata> <šifra> <iznos> <veličina>. 409 (već postoji) je u redu samo ako postojeći paket sa tim barkodom
# ima iste vrata, šifru i iznos; 409 se vraća i za zauzeta vrata ili šifru drugog paketa.
add() {
	body="{\"barkod\":\"$1\",\"brojPozicije\":$2,\"kodZaOtvaranje\":$3,\"iznosOtkupa\":$4,\"velicina\":\"$5\"}"
	code=$(curl -sS -o /dev/null -w '%{http_code}' -H 'Content-Type: application/json' -d "$body" "$url/packages")
	case "$code" in
		200) ;;
		409)
			if ! matches "$1" "$2" "$3" "$4"; then
				echo "Paket $1 (šifra $3, vrata $2, $4 RSD) nije dodat: zauzeta vrata/šifra ili postoji sa drugim vrednostima." >&2
				echo "Ponovo pokrenuti simulatore (Ctrl+C u terminalu 1, pa ./pokreni-simulatore.sh)." >&2
				exit 1
			fi
			;;
		*) echo "Dodavanje paketa $1 nije uspelo (HTTP $code)." >&2; exit 1 ;;
	esac
}

echo "$packages" | while read -r barcode door code amount size; do
	add "$barcode" "$door" "$code" "$amount" "$size" || exit 1
done || exit 1
echo ">>> Paketi za probu: 123456 (vrata 5), 234567 (vrata 7), 345678 (vrata 9, otkup 10 RSD)."
