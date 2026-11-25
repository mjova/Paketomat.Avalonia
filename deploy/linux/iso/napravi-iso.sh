#!/usr/bin/env bash
# ISO za instalaciju paketomata bez pitanja (doc/deploy-linux.md): zvanični Debian netinst + preseed u initrd-u + lokalni
# repozitorijum sa svim paketima kioska (instalacija ne preuzima ništa) + paket kioska. Jedino pitanje pri instalaciji je
# potvrda brisanja diska. Konfiguracija mašine se daje posle prvog boot-a (podesi-kiosk.sh).
#   deploy/linux/iso/napravi-iso.sh --paket artifacts/toppos-kiosk-<oznaka>-linux-x64.tar.gz
#       [--iso <debian-13.x-amd64-netinst.iso>] [--korisnik servis] [--izlaz Installer/iso]
# Radi na Linuxu (Debian/Ubuntu, i WSL) kao običan korisnik. Jednom: sudo apt-get install xorriso cpio debian-archive-keyring
# Lozinka servisnog korisnika se kuca pri pravljenju (ili TOPPOS_ISO_LOZINKA_HES = heš iz `openssl passwd -6`); u ISO ide
# samo heš. ISO zato nije za javno deljenje i ostaje van git-a (Installer/).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../../.." && pwd)

version=13
codename=trixie
arch=amd64
# Debian CD signing key (potpisuje SHA256SUMS zvaničnih ISO-a), https://www.debian.org/CD/verify
cd_key=DF9B9C49EAA9298432589D76DA87E80D6294BE9B
mirror=https://cdimage.debian.org/debian-cd/current/$arch/iso-cd
keyring=/usr/share/keyrings/debian-archive-keyring.gpg

say() { printf '\n=== %s\n' "$*"; }
die() { printf '\nGREŠKA: %s\n' "$*" >&2; exit 1; }

package=
iso=
user=servis
out_dir="$root/Installer/iso"
while [ $# -gt 0 ]; do
	case "$1" in
		--paket) package="$2"; shift 2 ;;
		--iso) iso="$2"; shift 2 ;;
		--korisnik) user="$2"; shift 2 ;;
		--izlaz) out_dir="$2"; shift 2 ;;
		*) die "Nepoznat argument: $1" ;;
	esac
done

[ "$(uname -s)" = Linux ] || die "Pokrenuti na Linuxu (npr. WSL: wsl -- bash deploy/linux/iso/napravi-iso.sh ...)."
for tool in xorriso cpio gzip curl gpg gpgv apt-get apt-cache dpkg-scanpackages openssl md5sum sha256sum; do
	command -v "$tool" > /dev/null || die "Nema alata $tool. Jednom: sudo apt-get install xorriso cpio dpkg-dev curl gpg openssl"
done
[ -f "$keyring" ] || die "Nema $keyring. Jednom: sudo apt-get install debian-archive-keyring"
[ -n "$package" ] && [ -f "$package" ] || die "--paket: paket kioska iz publish.sh (toppos-kiosk-*-linux-x64.tar.gz)."
package=$(cd "$(dirname "$package")" && pwd)/$(basename "$package")
case "$(basename "$package")" in
	toppos-kiosk-*-linux-x64.tar.gz) ;;
	*) die "--paket: očekuje se toppos-kiosk-*-linux-x64.tar.gz (ISO je za amd64)." ;;
esac
[[ "$user" =~ ^[a-z][a-z0-9-]{0,30}$ ]] || die "--korisnik: mala slova, cifre i crtica."
case "$user" in
	root|toppos) die "--korisnik: $user je rezervisan." ;;
esac

label=$(basename "$package" -linux-x64.tar.gz)
label=${label#toppos-kiosk-}
mkdir -p "$out_dir"
out_dir=$(cd "$out_dir" && pwd)
out="$out_dir/toppos-paketomat-debian$version-$label.iso"

cache="${XDG_CACHE_HOME:-$HOME/.cache}/toppos-iso"
mkdir -p "$cache"
work=$(mktemp -d "$cache/rad-XXXXXX")
trap 'rm -rf "$work"' EXIT

# ─── Lozinka servisnog korisnika (samo heš) ──────────────────────────────────────────────────────────────────────────
hash=${TOPPOS_ISO_LOZINKA_HES:-}
if [ -z "$hash" ]; then
	printf 'Lozinka korisnika %s na paketomatu: ' "$user"
	read -r -s password
	echo
	printf 'Ponovo: '
	read -r -s again
	echo
	[ -n "$password" ] || die "Prazna lozinka."
	[ "$password" = "$again" ] || die "Lozinke se ne poklapaju."
	hash=$(printf '%s' "$password" | openssl passwd -6 -stdin)
	unset password again
fi
# SHA-512 crypt kao iz `openssl passwd -6`: $6$[rounds=N$]so$ + sažetak od 86 znakova. Nepotpun heš bi ostavio uređaj bez
# servisne prijave (root je zaključan).
[[ "$hash" =~ ^\$6\$(rounds=[0-9]+\$)?[./0-9A-Za-z]{1,16}\$[./0-9A-Za-z]{86}$ ]] \
	|| die "Heš lozinke nije potpun SHA-512 crypt (openssl passwd -6)."

# ─── Paket kioska: spisak paketa (paketi.txt) iz istog izdanja ───────────────────────────────────────────────────────
say "Paket kioska $label"
packages_txt=$(tar -xzOf "$package" --wildcards '*/paketi.txt' 2> /dev/null) \
	|| die "U paketu nema paketi.txt (paket je stariji od ISO podrške) — napraviti nov sa deploy/linux/publish.sh."
tar -tzf "$package" --wildcards '*/install.sh' > /dev/null || die "U paketu nema install.sh."

# ─── Zvanični netinst ISO, proveren potpisom Debian CD ključa (i kad je dat sa --iso) ────────────────────────────────
# Aktuelno izdanje je u debian-cd/current, ranija tačkasta izdanja u cdimage/archive/<verzija>.
gnupg="$work/gnupg"
mkdir -m 700 "$gnupg"
curl -fsSL -o "$work/cd-key.asc" "https://keyring.debian.org/pks/lookup?op=get&search=0x$cd_key"
gpg --homedir "$gnupg" --quiet --import "$work/cd-key.asc" 2> /dev/null
gpg --homedir "$gnupg" --with-colons --fingerprint "$cd_key" | grep -q "^fpr:::::::::$cd_key:" \
	|| die "Preuzeti ključ nije Debian CD ključ $cd_key."
gpg --homedir "$gnupg" --export "$cd_key" > "$work/cd-key.gpg"
# signed_sums <url foldera>: SHA256SUMS sa ispravnim potpisom u $work/SHA256SUMS.
signed_sums() {
	curl -fsSL -o "$work/SHA256SUMS" "$1/SHA256SUMS" && curl -fsSL -o "$work/SHA256SUMS.sign" "$1/SHA256SUMS.sign" \
		|| return 1
	gpgv --keyring "$work/cd-key.gpg" "$work/SHA256SUMS.sign" "$work/SHA256SUMS" 2> /dev/null \
		|| die "Potpis SHA256SUMS ($1) nije ispravan."
}
if [ -z "$iso" ]; then
	say "Debian $version netinst ($mirror)"
	signed_sums "$mirror" || die "SHA256SUMS nije dostupan ($mirror)."
	name=$(grep -oE "debian-$version\.[0-9.]+-$arch-netinst\.iso" "$work/SHA256SUMS" | head -n 1)
	[ -n "$name" ] || die "Na $mirror nema Debian $version netinst ISO-a (novije izdanje?) — dati ga sa --iso."
	iso="$cache/$name"
	source_url=$mirror
else
	[ -f "$iso" ] || die "Nema $iso."
	name=$(basename "$iso")
	[[ "$name" =~ ^debian-($version\.[0-9.]+)-$arch-netinst\.iso$ ]] \
		|| die "--iso: očekuje se zvanični debian-$version.x-$arch-netinst.iso (ime fajla kao na debian.org)."
	say "Provera $name"
	source_url=$mirror
	if ! signed_sums "$mirror" || ! grep -q "  $name\$" "$work/SHA256SUMS"; then
		source_url="https://cdimage.debian.org/cdimage/archive/${BASH_REMATCH[1]}/$arch/iso-cd"
		signed_sums "$source_url" || die "SHA256SUMS za $name nije nađen ni u arhivi ($source_url)."
	fi
fi
expected=$(awk -v n="$name" '$2 == n { print $1 }' "$work/SHA256SUMS")
[ -n "$expected" ] || die "$name nije u potpisanom SHA256SUMS ($source_url)."
if [ ! -f "$iso" ] || [ "$(sha256sum "$iso" | cut -d' ' -f1)" != "$expected" ]; then
	[ "$iso" = "$cache/$name" ] || die "SHA256 za $iso se ne poklapa sa potpisanim ($source_url)."
	curl -fL --progress-bar -o "$iso.part" "$source_url/$name"
	mv "$iso.part" "$iso"
fi
[ "$(sha256sum "$iso" | cut -d' ' -f1)" = "$expected" ] || die "SHA256 za $name se ne poklapa."
echo "$name: SHA256 i potpis u redu."

# Korisničko ime koje instaler odbija (reserved-usernames iz user-setup-udeb ovog netinst-a, npr. admin) bi tražilo
# dodatno pitanje pri svakoj instalaciji.
mkdir "$work/user-setup"
xorriso -osirrox on -indev "$iso" -extract /pool/main/u/user-setup "$work/user-setup" 2> /dev/null || true
udeb=$(ls "$work"/user-setup/user-setup-udeb_*.udeb 2> /dev/null | head -n 1)
[ -n "$udeb" ] || die "U ISO-u nema user-setup-udeb (provera korisničkog imena)."
dpkg-deb -x "$udeb" "$work/user-setup/x"
reserved=$(find "$work/user-setup/x" -name reserved-usernames | head -n 1)
[ -n "$reserved" ] || die "U user-setup-udeb nema reserved-usernames."
if grep -v '^#' "$reserved" | grep -qx "$user"; then
	die "--korisnik: $user je rezervisano ime u Debian instaleru."
fi

# ─── Lokalni repozitorijum: paketi kioska i sistema sa svim zavisnostima ─────────────────────────────────────────────
# apt sa sopstvenim folderima (ne dira sistem na kome se pravi ISO, ne treba root); paketi su provereni potpisom arhive.
say "Lokalni repozitorijum (Debian $codename)"
apt_dir="$cache/apt-$codename"
mkdir -p "$apt_dir/lists/partial" "$apt_dir/archives/partial" "$apt_dir/apt.conf.d" "$apt_dir/prazno"
: > "$apt_dir/status"
cat > "$apt_dir/sources.list" << EOF
deb [signed-by=$keyring] https://deb.debian.org/debian $codename main non-free-firmware
deb [signed-by=$keyring] https://deb.debian.org/debian $codename-updates main non-free-firmware
deb [signed-by=$keyring] https://security.debian.org/debian-security $codename-security main non-free-firmware
EOF
cat > "$apt_dir/apt.conf" << EOF
Dir::State "$apt_dir";
Dir::State::lists "$apt_dir/lists";
Dir::State::status "$apt_dir/status";
Dir::Cache "$apt_dir";
Dir::Cache::archives "$apt_dir/archives";
Dir::Etc::main "/dev/null";
Dir::Etc::parts "$apt_dir/apt.conf.d";
Dir::Etc::sourcelist "$apt_dir/sources.list";
Dir::Etc::sourceparts "$apt_dir/prazno";
Dir::Etc::preferencesparts "$apt_dir/prazno";
Dir::Etc::trustedparts "$apt_dir/prazno";
APT::Architecture "$arch";
APT::Architectures { "$arch"; };
Acquire::Languages "none";
Debug::NoLocking "true";
EOF
export APT_CONFIG="$apt_dir/apt.conf"
apt-get -qq update

icu=$(apt-cache search --names-only '^libicu[0-9]+$' | awk '{ print $1 }' | sort -V | tail -n 1)
[ -n "$icu" ] || die "Paket libicuNN nije nađen."
wanted="$(printf '%s\n' "$packages_txt" | grep -v '^#' | xargs) $icu $(grep -v '^#' "$here/dodatni-paketi.txt" | xargs)"
echo "Traženi paketi: $wanted"
# Osnovni sistem (prioriteti required i important, koje instaler postavlja sa netinst-a) ide u repozitorijum u aktuelnim
# verzijama: toppos-late.sh ga prvo nadogradi (dist-upgrade), pa novi paketi ne traže verziju koje nema (npr. udev uz
# noviji libsystemd-shared).
base=$(apt-cache dumpavail | awk '/^Package:/ { p = $2 } /^Priority: (required|important)$/ { print p }' | sort -u | xargs)
# Zatvorenje zavisnosti (Depends i Pre-Depends, bez Recommends); višak ne smeta. Čisto virtuelni paketi se izostavljaju
# (njihovi davaoci su u spisku).
# shellcheck disable=SC2086
closure=$(apt-cache depends --recurse --no-recommends --no-suggests --no-conflicts --no-breaks --no-replaces \
	--no-enhances $wanted $base | grep -E '^[a-z0-9]' | sort -u)
debs=()
for p in $closure; do
	if apt-cache show --no-all-versions "$p" > /dev/null 2>&1; then
		debs+=("$p")
	fi
done
echo "Paketa sa zavisnostima: ${#debs[@]}"

stage="$work/iso"
mkdir -p "$stage/toppos/repo"
(cd "$stage/toppos/repo" && apt-get -qq download "${debs[@]}")
(cd "$stage/toppos/repo" && dpkg-scanpackages --multiversion . /dev/null 2> /dev/null | gzip -9 > Packages.gz)
echo "Repozitorijum: $(du -sh "$stage/toppos/repo" | cut -f1)"

# Servisni korisnik ne sme biti nalog ili grupa koju sistem već ima ili pravi (base-passwd, sysusers.d paketa iz
# repozitorijuma, npr. systemd-network): instaler bi nastavio sa postojećim nalogom bez prijave, a root je zaključan.
system_names=$(for deb in "$stage"/toppos/repo/*.deb; do
	dpkg-deb --fsys-tarfile "$deb" | tar -xOf - --wildcards './usr/lib/sysusers.d/*' \
		'./usr/share/base-passwd/passwd.master' './usr/share/base-passwd/group.master' 2> /dev/null || true
done | awk '$1 ~ /^[ugm]!?$/ { print $2; next } /^[^#].*:/ { split($0, f, ":"); print f[1] }' | sort -u)
[ -n "$system_names" ] || die "Spisak sistemskih naloga (base-passwd, sysusers.d) nije pročitan."
if printf '%s\n' "$system_names" | grep -qx "$user"; then
	die "--korisnik: $user je sistemski nalog ili grupa na Debianu."
fi
unset APT_CONFIG

cp "$package" "$stage/toppos/"
grep -v '^#' "$here/dodatni-paketi.txt" > "$stage/toppos/dodatni-paketi.txt"
echo "$codename" > "$stage/toppos/izdanje"
# ID izrade, isti u initrd-u i na ISO-u: toppos-early.sh proverava da je montiran baš ovaj ISO.
build_id="$label-$(date -u +%Y%m%d%H%M%S)-$RANDOM"
echo "$build_id" > "$stage/toppos/id"

# ─── Preseed u initrd, meni bez čekanja ──────────────────────────────────────────────────────────────────────────────
say "Instaler"
xorriso -osirrox on -indev "$iso" \
	-extract /install.amd/initrd.gz "$work/initrd.gz" \
	-extract /md5sum.txt "$work/md5sum.txt" 2> /dev/null
[ -f "$work/initrd.gz" ] || die "U ISO-u nema /install.amd/initrd.gz (nije Debian netinst?)."
bios=0
if xorriso -indev "$iso" -find /isolinux/isolinux.cfg 2> /dev/null | grep -q isolinux.cfg; then
	bios=1
fi
chmod u+w "$work"/initrd.gz "$work"/md5sum.txt

mkdir "$work/initrd-dodatak"
preseed=$(cat "$here/preseed.cfg")
preseed=${preseed//@KORISNIK@/$user}
preseed=${preseed//@LOZINKA_HES@/$hash}
printf '%s\n' "$preseed" > "$work/initrd-dodatak/preseed.cfg"
cp "$here/toppos-early.sh" "$here/toppos-late.sh" "$work/initrd-dodatak/"
echo "$build_id" > "$work/initrd-dodatak/toppos-id"
gunzip "$work/initrd.gz"
(cd "$work/initrd-dodatak" && printf '%s\n' preseed.cfg toppos-early.sh toppos-late.sh toppos-id \
	| cpio --quiet -H newc -o -A -F "$work/initrd")
gzip -9 "$work/initrd"

# Bez auto=true: preseed je u initrd-u, a auto bi tražio i preseed sa mreže.
boot_args="priority=critical vga=788 --- quiet"
title="TopPos paketomat - automatska instalacija (Debian $version)"
cat > "$work/grub.cfg" << EOF
set default=0
set timeout=0
menuentry '$title' {
	linux /install.amd/vmlinuz $boot_args
	initrd /install.amd/initrd.gz
}
EOF
cat > "$work/isolinux.cfg" << EOF
path
default toppos
prompt 0
timeout 0
label toppos
	menu label $title
	kernel /install.amd/vmlinuz
	append initrd=/install.amd/initrd.gz $boot_args
EOF

# md5sum.txt (provera medija u instaleru) za izmenjene i dodate fajlove.
md5_set() {
	local sum
	sum=$(md5sum "$1" | cut -d' ' -f1)
	grep -v "  \./${2#/}\$" "$work/md5sum.txt" > "$work/md5sum.novo" || true
	printf '%s  ./%s\n' "$sum" "${2#/}" >> "$work/md5sum.novo"
	mv "$work/md5sum.novo" "$work/md5sum.txt"
}
md5_set "$work/initrd.gz" /install.amd/initrd.gz
md5_set "$work/grub.cfg" /boot/grub/grub.cfg
[ "$bios" -eq 1 ] && md5_set "$work/isolinux.cfg" /isolinux/isolinux.cfg
(cd "$stage" && find toppos -type f -print0 | xargs -0 md5sum | sed 's|  toppos/|  ./toppos/|') >> "$work/md5sum.txt"

# ─── ISO: isti boot zapisi (BIOS i UEFI) kao zvanični, izmenjeni i dodati fajlovi ────────────────────────────────────
say "ISO"
maps=(-map "$work/initrd.gz" /install.amd/initrd.gz -map "$work/grub.cfg" /boot/grub/grub.cfg
	-map "$work/md5sum.txt" /md5sum.txt -map "$stage/toppos" /toppos)
[ "$bios" -eq 1 ] && maps+=(-map "$work/isolinux.cfg" /isolinux/isolinux.cfg)
rm -f "$out.part"
xorriso -indev "$iso" -outdev "$out.part" "${maps[@]}" -boot_image any replay 2> "$work/xorriso.log" \
	|| { cat "$work/xorriso.log" >&2; rm -f "$out.part"; die "xorriso nije uspeo."; }
mv "$out.part" "$out"
(cd "$out_dir" && sha256sum "$(basename "$out")" > "$(basename "$out").sha256")

say "Gotovo: $out ($(du -h "$out" | cut -f1))"
[ "$bios" -eq 1 ] || echo "UPOZORENJE: ISO nema isolinux — boot samo preko UEFI-ja."
echo "Korisnik: $user (sudo). U ISO-u je heš lozinke — ne deliti javno."
