#!/usr/bin/env bash
# Šta se diže pri boot-u: kiosk ili desktop. Menja se samo podrazumevani cilj i uključenost servisa toppos-kiosk; display
# manager (desktop) ostaje kakav jeste — u režimu kiosk se ne diže jer se graphical.target ne dostiže.
#   kiosk:   multi-user.target + toppos-kiosk (WantedBy=multi-user.target)
#   desktop: graphical.target (ako postoji display manager, inače multi-user.target), toppos-kiosk isključen
#   sudo /opt/toppos-kiosk/boot-mode.sh kiosk|desktop [--now]
# Važi od sledećeg boot-a; sa --now odmah (systemctl isolate: zaustavlja sve što novi cilj ne traži, npr. desktop sesiju).
# Hitan izlaz bez terminala (cage ne dozvoljava prelazak na drugi VT): doc/deploy-linux.md, GRUB parametri
# systemd.mask=toppos-kiosk.service systemd.unit=graphical.target.
set -euo pipefail

mode="${1:-}"
now=0
[ "${2:-}" = "--now" ] && now=1

if [ "$(id -u)" -ne 0 ]; then
	echo "Pokrenuti kao root (sudo)." >&2
	exit 1
fi

case "$mode" in
	kiosk)
		systemctl enable toppos-kiosk.service
		target=multi-user.target
		;;
	desktop)
		systemctl disable toppos-kiosk.service
		# Posle GRUB izlaza (systemd.mask=...) servis je maskiran, a disable nad maskiranim servisom ne briše link i vraća
		# uspeh — kiosk bi se digao već sledećeg boot-a (graphical.target uključuje multi-user.target).
		wants=/etc/systemd/system/multi-user.target.wants/toppos-kiosk.service
		rm -f "$wants"
		systemctl daemon-reload
		if [ -e "$wants" ] || [ -L "$wants" ]; then
			echo "Link $wants nije uklonjen — kiosk bi se i dalje dizao." >&2
			exit 1
		fi
		target=multi-user.target
		if [ -e /etc/systemd/system/display-manager.service ]; then
			target=graphical.target
		fi
		;;
	*)
		echo "Upotreba: $0 kiosk|desktop [--now]" >&2
		exit 2
		;;
esac

systemctl set-default "$target"
if [ "$now" -eq 1 ]; then
	if [ "$mode" = desktop ]; then
		systemctl stop toppos-kiosk.service
	fi
	systemctl isolate "$target"
fi
echo "Pri boot-u: $mode ($target)."
