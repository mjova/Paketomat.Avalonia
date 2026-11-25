#!/bin/sh
# partman/early_command (preseed.cfg; okruženje instalera, busybox sh), pre bilo kakve izmene diska: jedino pitanje
# instalacije. Operater bira disk koji se briše (model, serijski broj, veličina), podrazumevano je „Prekini“. Tek posle
# izbora se postavljaju način particionisanja, disk za partman i GRUB i potvrde, pa partman radi bez pitanja na tom disku.
# Svaki drugi ishod (Prekini, greška skripte, nejasan instalacioni medij, disk sa LVM-om ili RAID-om, zamenjen disk) gasi
# računar pre izmene bilo kog diska: posle neuspele komande Debian bi nastavio sa particionisanjem.
set -e
. /usr/share/debconf/confmodule

done_ok=0
trap '[ "$done_ok" = 1 ] || poweroff -f' EXIT

log() { logger -t toppos "$*"; }

cat > /tmp/toppos.templates << 'EOF'
Template: toppos/disk
Type: select
Choices: ${choices}
Description: Disk za paketomat se BRIŠE U CELOSTI
 Izabrati disk na koji se instalira paketomat. Sav sadržaj izabranog diska se briše bez daljih pitanja.
 .
 Ako disk nije na spisku ili niste sigurni, izabrati „Prekini“: nijedan disk se ne menja.

Template: toppos/prekinuto
Type: error
Description: Instalacija je prekinuta
 ${razlog}
 .
 Nijedan disk nije menjan. Računar se gasi; pre sledećeg uključivanja izvaditi USB ako instalacija nije potrebna.
EOF
debconf-loadtemplate toppos /tmp/toppos.templates

# stop <razlog>: poruka operateru i gašenje (bez izmene diska).
stop() {
	log "Prekid: $1"
	db_subst toppos/prekinuto razlog "$1" || true
	db_fset toppos/prekinuto seen false || true
	db_input critical toppos/prekinuto || true
	db_go || true
	poweroff -f
	exit 1
}

# phys <uređaj>: celi diskovi ispod uređaja (/dev/mapper/x → dm-N, particija → disk, dm/md → njegovi delovi, loop →
# disk sa fajlom). Prazno kad uređaj nije poznat.
phys() {
	local n f p s
	[ -n "$1" ] || return 0
	n=$(readlink -f "$1" 2> /dev/null || echo "$1")
	n=${n#/dev/}
	[ -e "/sys/class/block/$n" ] || return 0
	if [ -f "/sys/class/block/$n/loop/backing_file" ]; then
		f=$(cat "/sys/class/block/$n/loop/backing_file")
		phys "$(df -P "$f" 2> /dev/null | awk 'NR == 2 { print $1 }')"
		return 0
	fi
	if [ -n "$(ls "/sys/class/block/$n/slaves" 2> /dev/null)" ]; then
		for s in "/sys/class/block/$n/slaves"/*; do
			phys "/dev/${s##*/}"
		done
		return 0
	fi
	if [ -f "/sys/class/block/$n/partition" ]; then
		p=$(readlink -f "/sys/class/block/$n/..")
		echo "${p##*/}"
	else
		echo "$n"
	fi
}

# Instalacioni medij: ISO sa kog je učitan ovaj instaler (isti ID izrade u initrd-u i na /cdrom; sa dva Debian USB-a
# instaler može montirati drugi). Bez pouzdanog medija nema ni pouzdanog izuzimanja, pa se ne nastavlja.
[ -f /toppos-id ] && [ "$(cat /toppos-id)" = "$(cat /cdrom/toppos/id 2> /dev/null)" ] \
	|| stop "Montiran instalacioni medij nije ovaj TopPos ISO (više instalacionih USB-ova?). Ostaviti samo jedan."
media_dev=$(awk '$2 == "/cdrom" { print $1; exit }' /proc/mounts)
media=" $(phys "$media_dev" | tr '\n' ' ')"
[ -n "${media# }" ] || stop "Instalacioni medij (${media_dev:-nepoznat}) nije prepoznat kao disk."
log "Instalacioni medij: $media_dev (diskovi:$media)"

# signature <uređaj>: potpis sa direktnom sondom (blkid -p, bez keša) u $probe; uređaj mora biti čitljiv na početku i
# na kraju (RAID superblok 1.0/0.9 i rezervni GPT su na kraju), da greška čitanja ne izgleda kao prazan disk.
signature() {
	local dev=$1 sectors rc=0
	sectors=$(cat "/sys/class/block/${dev#/dev/}/size" 2> /dev/null || echo 0)
	dd if="$dev" of=/dev/null bs=512 count=2048 2> /dev/null || return 1
	if [ "$sectors" -gt 4096 ]; then
		dd if="$dev" of=/dev/null bs=512 skip=$((sectors - 2048)) count=2048 2> /dev/null || return 1
	fi
	probe=$(blkid -p -o export "$dev" 2> /dev/null) || rc=$?
	case "$rc" in
		0) return 0 ;;
		2) probe=; return 0 ;;
		*) return 1 ;;
	esac
}
# partitions <disk>: disk i njegove particije.
partitions() {
	echo "$1"
	ls -d "/sys/block/${1#/dev/}/${1#/dev/}"* 2> /dev/null | sed 's|.*/|/dev/|'
}

# RAID bilo gde u računaru zaustavlja instalaciju pre pitanja: partman pri pokretanju sastavlja pronađene nizove, a to može
# pisati (sinhronizacija) i po diskovima koje operater nije izabrao.
for disk in $(list-devices disk); do
	case "$media" in
		*" ${disk#/dev/} "*) continue ;;
	esac
	for dev in $(partitions "$disk"); do
		if signature "$dev" && echo "$probe" | grep -qE '^(USAGE=raid|TYPE=.*_raid_member)$'; then
			stop "Na disku $disk je RAID ($dev); instaler bi ga pokrenuo. Automatska instalacija ne radi na računaru sa RAID-om."
		fi
	done
done

# Spisak za izbor: „/dev/sda — 64 GB; Samsung SSD; S/N 123“ (zarez razdvaja izbore u debconf-u, pa ga u opisu nema).
# Za svaki disk se pamti identitet (diskseq, veličina, serijski broj) za proveru posle odgovora.
choices=
count=0
: > /tmp/toppos-diskovi
for disk in $(list-devices disk); do
	n=${disk#/dev/}
	case "$media" in
		*" $n "*) continue ;;
	esac
	case "$n" in
		mmcblk*boot*|mmcblk*rpmb) continue ;;
	esac
	[ "$(cat "/sys/block/$n/ro" 2> /dev/null)" = 1 ] && continue
	sectors=$(cat "/sys/block/$n/size" 2> /dev/null || echo 0)
	[ "$sectors" -gt 0 ] || continue
	size="$((sectors / 2097152)) GB"
	props=$(udevadm info --query=property --name="$disk" 2> /dev/null || true)
	# Samo unutrašnji diskovi (SATA, NVMe, eMMC): USB i prenosiv disk mogu biti zamenjeni u hodu, pa bi potvrđeno ime
	# (/dev/sdX) pre particionisanja moglo pripasti drugom disku. Kiosk se ne instalira na USB.
	# USB–SATA kućište može imati removable=0 i ID_BUS=ata: odlučuje USB predak u sysfs-u. Disk čije se poreklo ne može
	# utvrditi se takođe ne nudi.
	syspath=$(readlink -f "/sys/block/$n" 2> /dev/null || true)
	case "$syspath" in
		/sys/devices/*) ;;
		*) log "Preskočen disk $disk (poreklo nepoznato)."; continue ;;
	esac
	if [ "$(cat "/sys/block/$n/removable" 2> /dev/null)" = 1 ] || echo "$props" | grep -qx 'ID_BUS=usb' ||
		echo "$syspath" | grep -q '/usb[0-9]'; then
		log "Preskočen prenosiv/USB disk $disk ($syspath)."
		continue
	fi
	model=$(cat "/sys/block/$n/device/model" 2> /dev/null || cat "/sys/block/$n/device/name" 2> /dev/null || true)
	model=$(echo "$model" | sed 's/[[:space:]]*$//')
	serial=$(echo "$props" | sed -n 's/^ID_SERIAL_SHORT=//p' | head -n 1)
	[ -n "$serial" ] || serial=$(cat "/sys/block/$n/device/serial" 2> /dev/null || true)
	text=$(echo "$disk — $size${model:+; $model}${serial:+; S/N $serial}" | tr ',' ';')
	echo "$disk $(cat "/sys/block/$n/diskseq" 2> /dev/null || echo -) $sectors ${serial:--}" >> /tmp/toppos-diskovi
	choices="${choices:+$choices, }$text"
	count=$((count + 1))
done

[ "$count" -gt 0 ] || stop "Nema unutrašnjeg diska za instalaciju (USB i prenosivi diskovi se ne nude)."
db_subst toppos/disk choices "$choices, Prekini"
db_set toppos/disk Prekini
db_fset toppos/disk seen false
db_input critical toppos/disk || true
db_go || true
db_get toppos/disk
answer=$RET
chosen=${answer%% *}

case "$chosen" in
	/dev/*) ;;
	*) stop "Operater je izabrao „Prekini“." ;;
esac
n=${chosen#/dev/}

# Isti disk kao kad je pitanje prikazano (USB izvađen i drugi dobio isto ime → drugi diskseq ili serijski broj).
props=$(udevadm info --query=property --name="$chosen" 2> /dev/null || true)
serial=$(echo "$props" | sed -n 's/^ID_SERIAL_SHORT=//p' | head -n 1)
[ -n "$serial" ] || serial=$(cat "/sys/block/$n/device/serial" 2> /dev/null || true)
now="$chosen $(cat "/sys/block/$n/diskseq" 2> /dev/null || echo -) $(cat "/sys/block/$n/size" 2> /dev/null || echo 0) ${serial:--}"
grep -qxF "$now" /tmp/toppos-diskovi || stop "Disk $chosen se promenio dok je pitanje bilo otvoreno."

# LVM, RAID i btrfs (može obuhvatati više diskova) se ne brišu automatski: grupa, niz ili sistem datoteka mogu obuhvatati i
# druge diskove (Debianova provera LVM-a poredi putanje kao tekst, a nesastavljen RAID član ne vidi). Takav disk treba
# ručno očistiti pre instalacije. Greška čitanja ili nejasan ishod sonde takođe zaustavljaju.
for dev in $(partitions "$chosen"); do
	signature "$dev" || stop "Disk $chosen nije moguće pouzdano pročitati ($dev)."
	if echo "$probe" | grep -qE '^(USAGE=raid|TYPE=LVM2_member|TYPE=.*_raid_member|TYPE=btrfs)$'; then
		stop "Disk $chosen sadrži LVM, RAID ili btrfs ($dev). Automatska instalacija ga ne briše; očistiti ga ručno."
	fi
	name=${dev#/dev/}
	if [ -n "$(ls "/sys/class/block/$name/holders" 2> /dev/null)" ]; then
		stop "Disk $chosen je u upotrebi ($dev: LVM, RAID ili šifrovanje). Automatska instalacija ga ne briše."
	fi
done

log "Disk za instalaciju: $answer"
# preset <pitanje> <vrednost>: kao preseed (odgovor se ne pita). Pitanja postoje jer ih preseed.cfg navodi.
preset() {
	db_set "$1" "$2"
	db_fset "$1" seen true
}
preset partman-auto/disk "$chosen"
preset partman-auto/method regular
preset grub-installer/bootdev "$chosen"
# Izbor je izričita potvrda: dalje partman ne pita (i za disk bez prepoznatog sistema datoteka).
preset partman/confirm true
preset partman/confirm_nooverwrite true
done_ok=1
