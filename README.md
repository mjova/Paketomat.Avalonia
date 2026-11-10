# Paketomat v4 (Avalonia)

Kiosk aplikacija paketomata na Avalonia UI i .NET 10, za **Linux i Windows** iz istog koda.
Naslednik `Paketomat.v2` (WPF, samo Windows).

Status: početak — faza F0 (proba na Linuxu). Plan, odluke i procena su u [`doc/plan.md`](doc/plan.md).

## Struktura

| Folder | Sadržaj |
|---|---|
| `src/` | Jezgro (workflow, activator-i, servisi, kontroleri), platformski sloj (Windows/Linux) i Avalonia klijent |
| `tests/` | Workflow, platformski i E2E testovi |
| `tools/` | Simulatori (locker, server, POS) i pomoćni alati |
| `deploy/linux/` | Paket, instalacija, systemd servis sa kiosk sesijom (cage) — [`doc/deploy-linux.md`](doc/deploy-linux.md) |
| `deploy/windows/` | Instaler za Windows |
| `doc/` | Plan i dokumentacija |

Šta je preneto iz v2 i iz kog commit-a: [`PORTED_FROM.md`](PORTED_FROM.md).
E2E testovi (Windows i Linux): [`doc/e2e.md`](doc/e2e.md).
