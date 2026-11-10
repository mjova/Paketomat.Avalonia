#!/usr/bin/env bash
# Usklađivanje simulatora sa v2 (samo v2 → v4, dok v2 ne ode).
#
#   tools/sync-v2-simulators.sh <v2-repo> [--base <commit>] [--ref <grana>] [--apply]
#
# Prikazuje v2 commit-e posle poslednjeg pregledanog (--base; podrazumevano kolona „v2 commit" reda simulatora u
# PORTED_FROM.md) koji diraju simulatore, njihove testove, E2E klijente simulatora i protokolne biblioteke. Izmene
# fajlova koji u v4 postoje pod drugom putanjom (tabela ispod) primenjuje sa --apply kao 3-way merge (git apply -3);
# sukobi ostaju označeni u fajlu. Ostale putanje samo navodi — prenose se ručno. Posle pregleda upiši novi commit u
# PORTED_FROM.md.
#
# Primenjene izmene su u indeksu; sukob ostaje kao posle merge-a (git status: UU).
# 3-way merge traži osnovne verzije fajlova u v4 repou: skripta ih kopira iz v2 (git hash-object -w), bez fetch-a
# celog v2 repoa.
set -euo pipefail

usage() { sed -n '4p' "$0" | sed 's/^# *//' >&2; exit 1; }

[ $# -ge 1 ] || usage
v2="$1"; shift
base=""; ref="dev"; apply=0
while [ $# -gt 0 ]; do
	case "$1" in
		--base) base="$2"; shift 2 ;;
		--ref) ref="$2"; shift 2 ;;
		--apply) apply=1; shift ;;
		*) usage ;;
	esac
done

v4="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
if [ -z "$base" ]; then
	base="$(grep -E '^\| `tools/TopPos\.Simulators' "$v4/PORTED_FROM.md" | head -1 | awk -F'|' '{print $4}' | tr -d ' `')"
	[ -n "$base" ] || { echo "Red simulatora nije nađen u PORTED_FROM.md; zadaj --base." >&2; exit 1; }
fi
git -C "$v2" rev-parse --verify --quiet "$base^{commit}" >/dev/null || { echo "Nepoznat v2 commit: $base" >&2; exit 1; }

# v2 putanja → v4 putanja (fajlovi preneti doslovno pa prilagođeni)
mapped=(
	"TopPos.ServerSimulator/HttpServer.cs|tools/TopPos.Simulators.Server/HttpServer.cs"
	"TopPos.ServerSimulator/ServerFaults.cs|tools/TopPos.Simulators.Server/ServerFaults.cs"
	"TopPos.ServerSimulator/PackageItem.cs|tools/TopPos.Simulators.Server/PackageItem.cs"
	"TopPos.ServerSimulator/LogEntry.cs|tools/TopPos.Simulators.Server/LogEntry.cs"
	"TopPos.ServerSimulator/ServerSimulatorConfiguration.cs|tools/TopPos.Simulators.Server/ServerSimulatorConfiguration.cs"
	"TopPos.ServerSimulator/DoorViewModel.cs|tools/TopPos.Simulators.Server/DoorViewModel.cs"
	"TopPos.LockerSimulator/TcpLockController.cs|tools/TopPos.Simulators.Locker/TcpLockController.cs"
	"TopPos.LockerSimulator/ControlApiServer.cs|tools/TopPos.Simulators.Locker/ControlApiServer.cs"
	"TopPos.LockerSimulator/Locker.cs|tools/TopPos.Simulators.Locker/Locker.cs"
	"TopPos.LockerSimulator/LockerSimulatorConfiguration.cs|tools/TopPos.Simulators.Locker/LockerSimulatorConfiguration.cs"
	"TopPos.LockerSimulator/MainWindow.xaml.cs|tools/TopPos.Simulators.Locker/LockerEngine.cs"
	"TopPos.LockerSimulator/LockController.cs|tools/TopPos.Simulators.Locker/LockController.cs"
	"TopPos.LockerSimulator/BarcodeScanner.cs|tools/TopPos.Simulators.Locker/BarcodeScanner.cs"
	"TopPos.LockerSimulator/Pos/PosFrame.cs|tools/TopPos.Simulators.Locker/Pos/PosFrame.cs"
	"TopPos.LockerSimulator/Pos/PosFrameAssembler.cs|tools/TopPos.Simulators.Locker/Pos/PosFrameAssembler.cs"
	"TopPos.LockerSimulator/Pos/PosRequest.cs|tools/TopPos.Simulators.Locker/Pos/PosRequest.cs"
	"TopPos.LockerSimulator/Pos/PosResponses.cs|tools/TopPos.Simulators.Locker/Pos/PosResponses.cs"
	"TopPos.LockerSimulator/Pos/PosTerminalEngine.cs|tools/TopPos.Simulators.Locker/Pos/PosTerminalEngine.cs"
	"TopPos.LockerSimulator/Pos/PosTerminalModel.cs|tools/TopPos.Simulators.Locker/Pos/PosTerminalModel.cs"
	"TopPos.LockerSimulator/Pos/SerialPosWire.cs|tools/TopPos.Simulators.Locker/Pos/SerialPosWire.cs"
	"TopPos.LockerSimulator/Pos/SimCard.cs|tools/TopPos.Simulators.Locker/Pos/SimCard.cs"
	"Tests/TopPos.LockerSimulator.Tests/TcpLockControllerTests.cs|tests/TopPos.Simulators.Tests/TcpLockControllerTests.cs"
	"Tests/TopPos.LockerSimulator.Tests/Ecr.cs|tests/TopPos.Simulators.Tests/Ecr.cs"
	"Tests/TopPos.LockerSimulator.Tests/EcrCompatibilityTests.cs|tests/TopPos.Simulators.Tests/EcrCompatibilityTests.cs"
	"Tests/TopPos.LockerSimulator.Tests/FakeEcrWire.cs|tests/TopPos.Simulators.Tests/FakeEcrWire.cs"
	"Tests/TopPos.LockerSimulator.Tests/GoldenFrames.cs|tests/TopPos.Simulators.Tests/GoldenFrames.cs"
	"Tests/TopPos.LockerSimulator.Tests/PosEndToEndTests.cs|tests/TopPos.Simulators.Tests/PosEndToEndTests.cs"
	"Tests/TopPos.LockerSimulator.Tests/PosFrameAssemblerTests.cs|tests/TopPos.Simulators.Tests/PosFrameAssemblerTests.cs"
	"Tests/TopPos.LockerSimulator.Tests/PosFrameTests.cs|tests/TopPos.Simulators.Tests/PosFrameTests.cs"
	"Tests/TopPos.LockerSimulator.Tests/PosRequestTests.cs|tests/TopPos.Simulators.Tests/PosRequestTests.cs"
	"Tests/TopPos.LockerSimulator.Tests/PosTerminalEngineTests.cs|tests/TopPos.Simulators.Tests/PosTerminalEngineTests.cs"
)
# Prate se, ali se prenose ručno (GUI, E2E klijenti, protokol; biblioteke iz src/ imaju svoje redove u PORTED_FROM.md)
watched=(
	TopPos.ServerSimulator
	TopPos.LockerSimulator
	Tests/TopPos.LockerSimulator.Tests
	Tests/TopPos.E2E.Tests/Infrastructure/ServerSim.cs
	Tests/TopPos.E2E.Tests/Infrastructure/LockerSim.cs
	Tests/TopPos.E2E.Tests/Config
	TopPos.LockController.NCU24L
	TopPos.Service
	TopPos.PenPosService
)

echo "v2: $base..$ref"
commits="$(git -C "$v2" log --oneline "$base..$ref" -- "${watched[@]}")"
if [ -z "$commits" ]; then
	echo "Nema izmena posle $base."
	exit 0
fi
echo "$commits"
echo

declare -A is_mapped=()
conflicts=0
for pair in "${mapped[@]}"; do
	src="${pair%%|*}"; dst="${pair##*|}"
	is_mapped["$src"]=1
	patch="$(git -C "$v2" diff --full-index "$base" "$ref" -- "$src")"
	[ -n "$patch" ] || continue
	echo "== $src → $dst"
	git -C "$v2" diff --stat "$base" "$ref" -- "$src" | head -1
	[ "$apply" = 1 ] || continue
	# osnovna verzija za 3-way merge
	old_blob="$(git -C "$v2" rev-parse "$base:$src")"
	git -C "$v2" cat-file blob "$old_blob" | git -C "$v4" hash-object -w --stdin >/dev/null
	if printf '%s\n' "$patch" | sed -e "s#a/$src#a/$dst#g" -e "s#b/$src#b/$dst#g" | (cd "$v4" && git apply -3 --whitespace=nowarn); then
		echo "   primenjeno"
	else
		echo "   SUKOB — razreši označena mesta u $dst"
		conflicts=1
	fi
done

echo
echo "Ručni pregled (nema mapiranu v4 putanju):"
git -C "$v2" diff --name-only "$base" "$ref" -- "${watched[@]}" | while read -r f; do
	[ -n "${is_mapped[$f]:-}" ] || echo "   $f"
done

if [ "$apply" != 1 ]; then
	echo
	echo "Bez --apply ništa nije menjano."
fi
exit $conflicts
