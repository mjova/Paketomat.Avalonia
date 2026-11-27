#!/usr/bin/env bash
# Linux E2E (odluka D4): test klijent kanala ka Agentu radi kao root kroz sudo bez lozinke. Pravi samostalnu kopiju klijenta
# koju menja samo root (/usr/local/lib/toppos-e2e/agent-client) i sudo pravilo samo za nju, za tekući nalog.
#
# SAMO za E2E VM, za nalog koji već ima pun sudo (sa lozinkom — bez njega skripta ni ne prolazi): pravilo mu samo uklanja
# lozinku za ovaj klijent. Klijent pod sudo sme samo kanal ka kiosku koji radi pod tim nalogom i prođe proveru (tačka 4
# ugovora): izjava i socket se otvaraju pravima pozivaoca, --layout, --result-file i --squat se odbijaju. To ograničava
# slučajnu zloupotrebu, nije zaštita od tog naloga (on ionako ima sudo). Kopija iz build izlaza ne sme u pravilo: nalog bi je
# mogao zameniti, pa bi pravilo bez lozinke važilo za bilo koji program.
#
# Pokretanje (kao E2E nalog, ne kao root): tools/install-e2e-agent-client.sh — sudo traži lozinku za instalaciju.
# Posle izmene klijenta (tools/TopPos.AgentPipe.TestClient) pokrenuti ponovo. Uklanjanje:
#   sudo rm -rf /usr/local/lib/toppos-e2e/agent-client /etc/sudoers.d/toppos-e2e-agent-client
set -euo pipefail

repo=$(cd "$(dirname "$0")/.." && pwd)
user=$(id -un)
if [ "$(id -u)" -eq 0 ]; then
	echo "Pokrenuti kao E2E nalog, ne kao root (pravilo važi za nalog koji pokreće skriptu)." >&2
	exit 1
fi
case "$(uname -m)" in
	x86_64) rid=linux-x64 ;;
	aarch64) rid=linux-arm64 ;;
	*) echo "Nepodržana arhitektura $(uname -m)." >&2; exit 1 ;;
esac

target=/usr/local/lib/toppos-e2e/agent-client
client=$target/TopPos.AgentPipe.TestClient
rule_file=/etc/sudoers.d/toppos-e2e-agent-client

# .NET SDK: sa PATH-a, inače iz ~/.dotnet (kao run-e2e.sh)
if ! command -v dotnet > /dev/null; then
	if [ -x "$HOME/.dotnet/dotnet" ]; then
		export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"
	else
		echo ".NET SDK (dotnet) nije na PATH-u ni u ~/.dotnet." >&2
		exit 1
	fi
fi

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

# Samostalan jedan fajl: root ne zavisi od dotnet-a u nalogu korisnika (sudo briše okruženje).
dotnet publish "$repo/tools/TopPos.AgentPipe.TestClient/TopPos.AgentPipe.TestClient.csproj" -c Release -r "$rid" \
	--self-contained -p:PublishSingleFile=true -p:DebugType=none -o "$work/publish" >/dev/null

sudo install -d -o root -g root -m 0755 /usr/local/lib/toppos-e2e "$target"
sudo install -o root -g root -m 0755 "$work/publish/TopPos.AgentPipe.TestClient" "$client"

printf '%s ALL=(root) NOPASSWD: %s\n' "$user" "$client" > "$work/rule"
sudo visudo -cf "$work/rule" >/dev/null
sudo install -o root -g root -m 0440 "$work/rule" "$rule_file"

# Bez keširane lozinke i bez okruženja korisnika (DOTNET_ROOT): radi samo pravilo i samostalna kopija.
sudo -k -n "$client" --self-check
echo "Instalirano: $client (sudo bez lozinke za $user, $rule_file)."
