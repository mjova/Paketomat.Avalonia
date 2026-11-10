# E2E testovi

Pravi kiosk (`TopPos.Kiosk`) nad simulatorima servera i paketomata (`TopPos.Simulators both`), sa upravljanjem kroz
pristupačnost platforme: Windows — UI Automation (FlaUI), Linux — AT-SPI preko D-Bus-a. Isti scenariji na obe
platforme. Preneto iz v2 `Tests/TopPos.E2E.Tests` (v2 `doc/plan-e2e-ui-testing.md`); šta je preneto i kako se
usklađuje: `PORTED_FROM.md` i `tools/sync-v2-simulators.sh`.

Projekat: `tests/TopPos.Kiosk.E2E.Tests`. Bez `TOPPOS_E2E=1` testovi se preskaču (CI ih gradi, ne pokreće — traže
otključan desktop).

## Pokretanje

| Platforma | Komanda |
|---|---|
| Windows | `run-e2e.cmd` |
| Linux | `./run-e2e.sh` |

Skripta prvo pušta testove jezgra kioska (ako padnu, E2E se ne pokreće), gradi rešenje (Debug) i pušta E2E sa
izveštajem u `TestResults/E2E` (HTML i TRX). Dodatni argumenti idu u `dotnet test`, npr.
`--filter "FullyQualifiedName~RecipientPaymentTests"`. Ceo skup traje oko 30 min po platformi (mnogi scenariji namerno
čekaju istek od 20–30 s).

## Preduslovi

**Windows**
- com0com parovi: skener COM18 (kiosk) ↔ COM19 (simulator), POS COM20 ↔ COM21; za `SerialControllerTests` i COM22 ↔
  COM23 (bez njega se ti testovi preskaču). Drugi parovi: `TOPPOS_E2E_SCANNER_PORTS`, `TOPPOS_E2E_POS_PORTS`,
  `TOPPOS_E2E_LOCKER_PORTS` (npr. `COM30,COM31`).
- Otključan desktop; prozor kioska (1024×768 iz šablona) mora stati na ekran — `run-e2e.cmd` proverava
  (`check-screen.ps1`). Tokom izvršavanja ne dirati miš i tastaturu.
- Testovi sa pravim mišem (`[RealMouseFact]`, `[RealMouseTheory]`: Home tokom unosa i plaćanja, dug pritisak, promena
  jezika) pomeraju miš.

**Linux**
- .NET 10 SDK na PATH-u; ako je instaliran u `~/.dotnet` (dotnet-install), `run-e2e.sh` ga sam koristi.
- Grafička sesija (X11 ili Wayland) i D-Bus sesija; testovi se puštaju iz terminala te sesije. Harness pri prvom startu
  kioska uključuje pristupačnost u sesiji (`org.a11y.Status.IsEnabled`) i ona ostaje uključena.
- Serijski uređaji idu preko pty parova koje pravi simulator (`KioskPortName`, putanje u `/tmp/toppos-e2e/dev`) —
  com0com nije potreban. `SerialControllerTests` rade.
- Testovi sa pravim mišem se preskaču (AT-SPI nema klik mišem, Wayland ne dozvoljava ubacivanje).
- ~2 GB slobodno u `/tmp` (`run-e2e.sh` proverava).

**Obe platforme:** ne sme da radi razvojna instanca kioska (Windows: mutex `TopPos.Client.WPF.SingleInstance`, isti kao
v2; Linux: `/run/lock/toppos-kiosk.lock`) — harness inače odbija start.

## Kako radi

Svaki test je jedna sesija (`KioskSession`):
1. Brava sesije (`<temp>/toppos-e2e/session.lock`) — jedna sesija u isto vreme, i između runner-a.
2. Sandbox `<temp>/toppos-e2e/<vreme>-<test>/` sa kopijama bin-a simulatora i kioska, bez runtime stanja.
3. `TopPos.Simulators both` na slobodnim portovima, sa `--parent-pid` (ne nadživi test proces); harness čita READY red
   (portovi, serijski portovi kioska).
4. Konfiguracija kioska iz `Config/client.app-configuration.json` sa tokenima (`{{ServerPort}}`, `{{LockerTcpPort}}`,
   serijski portovi...) i izmenama po testu (`KioskOptions`); kiosk se pokreće i čeka se `Page.StartPage` i smirivanje
   loga.
5. Scenario: UI po AutomationId-u (`Page.<Stranica>`, `<Stranica>.<Komanda>`, `Keypad.*`, `Header.*`), simulatori preko
   kontrolnog API-ja (`/_sim/*`), stanje mašine iz loga kioska (`Workflow: A --[T]--> B`).
6. Opšte provere (koordinator, `status.lock`), gašenje procesa. Sandbox testa koji je prošao se briše
   (`TOPPOS_E2E_KEEP=1` čuva sve).

Uredno gašenje kioska: Windows — zatvaranje glavnog prozora (WM_CLOSE), Linux — SIGTERM.

Testovi tokova kojih v4 još nema (kurir, pošiljalac, istekli paketi; kraj Windows sesije) ostaju u fajlovima prenetim
iz v2 sa `[PendingFlow]` i preskaču se bez pokretanja sesije; pomoćne metode tih tokova imaju privremene zamene u
`Scenarios/PendingFlows.cs`.

## Pri padu

U sandbox-u palog testa:
- `e2e-trace.log` — koraci testa sa vremenom (isto ide u izlaz testa);
- `failure.png` (Windows) ili `failure.atspi.txt` (Linux: stablo pristupačnosti — AccessibleId, ime, stanje);
- `client/Logs/log-*.txt` — log kioska (rep ide i u izlaz testa);
- `simulators.log` (stdout/stderr simulatora) i `simulators/Logs/simulators-*.txt` (Debug log, uz prijem i čitanje tela
  svakog zahteva servera).

## Promenljive okruženja

| Promenljiva | Značenje |
|---|---|
| `TOPPOS_E2E=1` | uključuje E2E testove (skripte je postavljaju) |
| `TOPPOS_E2E_KEEP=1` | čuva sandbox i testova koji su prošli |
| `TOPPOS_E2E_SCANNER_PORTS`, `TOPPOS_E2E_POS_PORTS`, `TOPPOS_E2E_LOCKER_PORTS` | Windows: com0com parovi `kiosk,simulator` |

## Poznata ograničenja

- Testovi sa pravim mišem rade samo na Windows-u.
- Na Windows-u je jednom (10.11.2026) zahtev harness-a ka simulatoru servera obrađen tek posle 14 s (harness čeka
  10 s); uzrok nije nađen — simulator od tada beleži prijem i čitanje tela zahteva.
- Kraj Windows sesije (WM_QUERYENDSESSION) v4 još ne obrađuje (F7).
