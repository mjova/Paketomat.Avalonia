#!/bin/sh
# Glavni proces servisa toppos-kiosk: pokreće cage sa kioskom i upravlja gašenjem. Servis ima KillMode=mixed, pa SIGTERM
# (systemctl stop/restart) dobija samo ova skripta. Ona ga prosleđuje kiosku i čeka da kiosk završi (zatvaranje
# kontrolera, završni upis u status.lock); kad kiosk izađe, cage izlazi sam. Da signal ide svima odjednom, cage bi ugasio
# prikaz dok kiosk još radi. Granica čekanja je TimeoutStopSec servisa (posle nje SIGKILL svima).
program_dir=$(dirname "$(readlink -f "$0")")

/usr/bin/cage -d -- "$program_dir/TopPos.Kiosk" &
cage_pid=$!

stop() {
	kiosk_pid=$(pgrep -P "$cage_pid" -x TopPos.Kiosk | head -n 1)
	if [ -n "$kiosk_pid" ]; then
		kill -TERM "$kiosk_pid" 2> /dev/null
	else
		# Kiosk još nije pokrenut ili je već izašao: nema šta da se završava.
		kill -TERM "$cage_pid" 2> /dev/null
	fi
}
trap stop TERM INT

# wait se prekida kad stigne signal (posle obrade); čeka se dok cage stvarno ne izađe.
while :; do
	wait "$cage_pid"
	status=$?
	kill -0 "$cage_pid" 2> /dev/null || break
done
exit "$status"
