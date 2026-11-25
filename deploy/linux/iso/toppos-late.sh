#!/bin/sh
# preseed/late_command (preseed.cfg; okruženje instalera, busybox sh): posle osnovnog sistema instalira pakete kioska iz
# lokalnog repozitorijuma sa ISO-a (bez mreže) i sam kiosk (install.sh, bez konfiguracije mašine i bez boot-a u kiosk —
# to radi podesi-kiosk.sh posle prvog boot-a). Na kraju izvori za ažuriranja preko mreže. Sadržaj /cdrom/toppos pravi
# napravi-iso.sh. Ispis ide u /var/log/toppos-instalacija.log na instaliranom sistemu.
set -e

src=/cdrom/toppos
log=/target/var/log/toppos-instalacija.log
exec >> "$log" 2>&1
echo "=== $(date) toppos-late.sh"

codename=$(cat "$src/izdanje")
repo=/var/lib/toppos-offline
setup=/var/lib/toppos-instalacija

# Lokalni repozitorijum je jedini izvor za ovu instalaciju (APT_CONFIG): bez mreže i bez cdrom izvora instalera.
# trusted=yes: paketi su pri pravljenju ISO-a preuzeti preko apt-a uz proveru potpisa Debian arhive.
mkdir -p "/target$repo" "/target$setup"
cp -a "$src/repo/." "/target$repo/"
echo "deb [trusted=yes] file:$repo ./" > "/target$setup/offline.list"
cat > "/target$setup/apt.conf" << EOF
Dir::Etc::sourcelist "$setup/offline.list";
Dir::Etc::sourceparts "-";
APT::Get::List-Cleanup "false";
EOF
offline="env APT_CONFIG=$setup/apt.conf DEBIAN_FRONTEND=noninteractive"

in-target $offline apt-get update
# Osnovni sistem na verzije iz repozitorijuma (ažuriranja posle izdanja netinst-a), da novi paketi ne traže drugačiju
# verziju već instaliranih (npr. udev i libsystemd-shared).
in-target $offline apt-get -y dist-upgrade
# shellcheck disable=SC2046
in-target $offline apt-get install -y --no-install-recommends $(cat "$src/dodatni-paketi.txt")

# Kiosk: install.sh iz paketa (apt-get update/install idu u isti lokalni repozitorijum).
cp "$src"/toppos-kiosk-*.tar.gz "/target$setup/"
in-target sh -c "cd $setup && tar -xzf toppos-kiosk-*.tar.gz && rm toppos-kiosk-*.tar.gz"
in-target sh -c "cd $setup/toppos-kiosk-* && $offline ./install.sh"

# Izvori za kasnija ažuriranja (mreža) se uključuju tek pri prvom boot-u: završni koraci instalera posle late_command
# (apt-cdrom-setup) pokreću apt-get update nad aktivnim izvorima, a instalacija ne sme da zavisi od mreže.
cat > /target/etc/apt/toppos-izvori.list << EOF
deb http://deb.debian.org/debian $codename main non-free-firmware
deb http://deb.debian.org/debian $codename-updates main non-free-firmware
deb http://security.debian.org/debian-security $codename-security main non-free-firmware
EOF
cat > /target/etc/systemd/system/toppos-apt-izvori.service << 'EOF'
[Unit]
Description=TopPos: izvori za ažuriranja (prvi boot posle instalacije sa ISO-a)
ConditionPathExists=/etc/apt/toppos-izvori.list

[Service]
Type=oneshot
ExecStart=/bin/mv /etc/apt/toppos-izvori.list /etc/apt/sources.list.d/debian.list

[Install]
WantedBy=multi-user.target
EOF
in-target systemctl enable toppos-apt-izvori.service

# Mreža: ako DHCP tokom instalacije nije uspeo (kabl nije bio priključen), instaler upisuje samo loopback. Svaki žičani
# interfejs bez podešavanja dobija DHCP čim se kabl priključi, da SSH za podešavanje radi.
interfaces=/target/etc/network/interfaces
for path in /sys/class/net/*; do
	iface=${path##*/}
	[ -e "$path/device" ] && [ "$(cat "$path/type")" = 1 ] && [ ! -d "$path/wireless" ] || continue
	if ! grep -q "^iface $iface " "$interfaces" 2> /dev/null; then
		printf '\nallow-hotplug %s\niface %s inet dhcp\n' "$iface" "$iface" >> "$interfaces"
		echo "DHCP za $iface (instalacija bez mreže na tom interfejsu)"
	fi
done

rm -rf "/target$repo" "/target$setup"
echo "=== $(date) gotovo"
