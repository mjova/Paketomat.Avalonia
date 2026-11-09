# Plan: Paketomat v4 — Avalonia za Linux i Windows

> Putanje i reference `fajl:linija` u ovom planu odnose se na **`Paketomat.v2`** na commit-u `0029647`, osim gde
> piše drugačije. Plan je prenet iz v2 (`doc/plan-linux.md`) 9.11.2026.

Status: **predlog**, ništa nije urađeno. Nastalo 9.11.2026 iz pregleda koda v2 na grani `dev` (`0029647`), dopunjeno
Codex review-om (sekcija 9). Prvi korak je proba na Linuxu (F0); nastavak zavisi od odluke klijenta. Cilj je da v4
bude naslednik v2 na **obe** platforme.

---

## 1. Odluke

| Odluka | Izbor |
|---|---|
| UI | **Avalonia UI, besplatni (MIT) paket.** Plaćeni paketi ili XPF samo ako proba pokaže konkretnu potrebu |
| .NET | **.NET 10 LTS** (.NET 9 izlazi iz podrške 10.11.2026) |
| Projekat | **Zaseban**: `E:\VS\TopPos\Paketomat.v4.Avalonia`, sopstveni GitHub repo i solution. `Paketomat.v2` (WPF, Windows) ostaje netaknut |
| Platforme | **Linux i Windows** iz istog koda; platformske razlike u tankom sloju (sekcija 1a) |
| Proba | F0 je **početak pravog projekta**, ne prototip za bacanje; radi se na Linuxu jer je tamo rizik |

### Posledica zasebnog projekta

Jezgro (workflow, activator-i, servisi, kontroleri, ECR, PenPOS, ugovor sa Agentom) se **kopira** iz v2 i tamo menja.
Isti model kao `Tools/TopPos.Technician` — duplikacija je namerna, ali **vremenski ograničena**.

- Prednost: v2 i produkcija na Windows-u ne trpe nikakav rizik; nema prerade v2 radi apstrakcija.
- Cena: dok Windows flota ne pređe na v4 (F7), ispravke se prenose ručno između dva koda.
- Pravilo: v4 vodi `PORTED_FROM.md` — iz kog v2 commit-a je koji deo kopiran; pri svakom v2 izdanju proveriti šta
  od izmena treba preneti.
- Posle F7: v2 dobija samo ispravke grešaka dok poslednji Windows paketomat ne pređe, zatim se gasi.

## 1a. Platformski sloj

Sve razlike između Windows-a i Linuxa idu iza interfejsa sa dve implementacije; ostatak koda ne sme da proverava
platformu. Windows implementacije se uglavnom prenose iz v2.

| Interfejs (radni naziv) | Windows | Linux |
|---|---|---|
| `IAgentChannelTransport` | named pipe + SID, `FirstPipeInstance` (iz v2 `AgentCommunication/`) | Unix socket + `SO_PEERCRED` |
| `IShutdownSignal` | kraj Windows sesije — **isti redosled kao v2** (završni upis pre odgovora Windows-u, 51f479b) | SIGTERM (`PosixSignalRegistration`) |
| `ISingleInstanceGuard` | named mutex (`Global\` ako ceo uređaj) | `flock` u `/run` |
| `IProcessInfo` | `kernel32` (iz v2 `StatusDeclaration`) | `/proc/self/stat` |
| `IAppPaths` | `C:\LockerStation\...` | `/opt`, `/etc`, `/var/lib`, `/var/log` |
| `ISerialPortCatalog` | `COMn` | `/dev/serial/by-id`, `/dev/ttyUSB*`/`ttyACM*` |
| `IAudioPlayer` | izabrana besplatna biblioteka, ista na obe ako može | GStreamer / LibVLCSharp / `paplay` |
| Isporuka | InnoSetup (iz v2) | `.deb` + systemd |

- CI gradi i pokreće testove na **obe** platforme od F0.
- Razvoj teče na Windows-u sa simulatorima — v4 mora stalno da radi i tamo.
- Proveriti: da li Avalonia na Windows-u daje istu garanciju kod kraja sesije kao WPF `OnSessionEnding`; ako ne,
  Windows implementacija `IShutdownSignal` hvata `WM_QUERYENDSESSION` direktno.

## 2. Polazno stanje (v2)

| Deo | Stanje | Posledica |
|---|---|---|
| `TopPos.Service`, `TopPos.LockController*`, `TopPos.ECR`, `TopPos.PenPosService`, `Flatsoft.Common`, `TopPos.Agent.Contracts` | `net9.0` | Kopira se; proveriti ponašanje na Linuxu |
| `TopPos.Client.WPF/Workflow/` | bez `System.Windows`, ali `StatusDeclaration.cs` ima kernel32 P/Invoke | Kopira se uz zamenu P/Invoke-a |
| `MainWindow/` (activator-i, `IWorkflowHost`) | vezano za WPF: `IWorkflowHost.cs:56` izlaže `UserControl`, `RecipientFlowActivator.cs:40` pravi WPF stranice | Navigacija i fabrika prikaza se prave iznova |
| ViewModel-i | delom WPF tipovi: `RecipientFiscalReceiptViewModel.cs:64` (`BitmapImage`), `RemoteServiceViewModel.cs:445` (`SolidColorBrush`), `Flatsoft.Common.UI/RelayCommand.cs` (`CommandManager`) | Neutralni tipovi ili Avalonia ekvivalenti |
| XAML klijenta | 160 fajlova (~13k linija), 20 stranica, 6 layouta (`ResourceDictionary` + `TemplateSelector`), brendovi preko `Brands/<Brand>/theme.xaml` | Prepis. ~8 `Storyboard` deklaracija (`Resources/Common.xaml`, `Styles.xaml`, Barcode, QRCode), `EventTrigger`/`BeginStoryboard` (`Common.xaml:45`), template triggeri (`NumericKeypad.xaml:29`) |
| `LanguageManager` | `Flatsoft.Common/LanguageManager.cs:14`, već neutralan | Kopira se |
| `Flatsoft.Common.UI/ButtonPressBehavior.cs` | mouse događaji (`:38`), `Mouse.LeftButton` (`:62`) | Prepis na pointer/touch, sa zabranom tokom plaćanja |
| LockerSimulator, ServerSimulator, Configurator, Technician | WPF | Simulatori potrebni za razvoj; ostali kasnije |
| `Tests/TopPos.Workflow.Tests` | WPF tip projekta, `AgentPipeServerTests.cs:22` statički `WindowsIdentity` | Kopirati čiste testove |
| `Tests/TopPos.E2E.Tests` | FlaUI, zasebni procesi (`KioskSession.cs:419`), psexec (`LocalSystemAgentClient.cs:60`) | Nov harness |
| Build | `TopPos.Client.WPF.csproj:95` zove `git.exe`; `global.json` SDK 9.0.306 | `git` bez `.exe`, .NET 10 |

## 3. Windows-specifična mesta i zamene

1. **Kanal ka Agentu** — `AgentPipeSecurity.cs`, `AgentPipeServer.cs` (`FirstPipeInstance`, `:114`),
   `AgentPipeConnection.cs` (provera identiteta klijenta, `:153`). Named pipe na Linuxu = Unix domain socket; nema
   `PipeSecurity`, SID-ova, `WindowsIdentity`, `advapi32`. Zamena: prava na socket fajlu + `SO_PEERCRED` (uid/pid).
   Agent proverava PID servera (`Tools/TopPos.AgentPipe.TestClient/Program.cs:458`). Dogovoriti: obostrani identitet,
   vlasnik i lokacija socket-a, zauzeto/zastarelo ime, redosled starta i gašenja. Zahtev ide u
   `doc/notes/Info/Ugovor pipe komandi/`. `AgentProtocol.Version` se ne menja.
2. **Gašenje** — `App.xaml.cs` (`OnSessionEnding`), `LockControllerShutdown.cs` → SIGTERM od systemd-a
   (`PosixSignalRegistration`) + `TimeoutStopSec`. Logika „završni upis pa izlaz" se prenosi.
3. **`StatusDeclaration`** — `kernel32` (`:53`) → `Process.StartTime` ili `/proc/self/stat`. Značenje `status.lock`
   izjave ostaje isto.
4. **Jedna instanca** — `App.xaml.cs:113` named mutex; na Unix-u je od .NET 9.0.21 podrazumevani opseg korisnički.
   Odlučiti: ceo uređaj ili sesija; verovatno `flock` na fajlu u `/run`.
5. **Putanje i stanje** — jezici preko Windows literala (`App.xaml.cs:250`), `status.lock` uz binarije
   (`App.xaml.cs:162`), redovi potvrda relativni (`AppConfiguration.cs:166`), ECR piše `Log\...` u `BaseDirectory`
   (`TopPos.ECR/ECR/EcrLog.cs:78`). Razdvojiti: program `/opt/...`, konfiguracija `/etc/...`, stanje i redovi
   `/var/lib/...`, logovi `/var/log/...`. Linux razlikuje velika i mala slova u imenima fajlova.
6. **Zvuk** — `Flatsoft.Common.UI/AudioPlayer.cs` (WPF `MediaPlayer`) → besplatno: GStreamer, LibVLCSharp ili `paplay`.
7. **QR** — `QRCodeService.cs` (`System.Drawing.Common`, `BitmapImage`) → `QRCoder` PNG → Avalonia `Bitmap`.
8. **Serijski portovi** — `Flatsoft.Common/SerialClient.cs:278` već čita preko `BaseStream.ReadAtLeastAsync`; POS V3
   koristi `ReadByte` (`CommunicationPortV3.cs:278`). Nova petlja nije potrebna unapred. Proveriti: timeout,
   cancellation, isključivanje/uključivanje USB-a, drajvere adaptera. Portovi `/dev/ttyUSB*`/`/dev/ttyACM*`, stalna
   imena kroz udev (`/dev/serial/by-id`), korisnik u grupi `dialout`.
9. **ECR** — klijent nameće Ingenico (`PosClient.cs:159`) i V3 (`PosClient.cs:380`). Registry pretraga po VID/PID
   (`CommunicationPortV3.cs:60`) radi samo za Castles; `hwid.cs` (WMI) se poziva samo iz `calculateProdKey`
   (`EcrPortV1.cs:669`) za koji nema poziva. **Nije bloker**; uslovni rizik ako se uvede drugi POS.
10. **Configurator** — `ToLower()` nad imenom porta (`ControllerViewModel.cs:81`, `PosViewModel.cs:60`,
    `ScannerViewModel.cs:36`) i lista COM1–COM25 (`Shared/CfgParameters.cs:31`) → enumeracija Linux portova, bez
    menjanja velikih/malih slova.
11. **Fiskalizacija** — `RecipientFlowActivator.cs:542` traži račun, `PenPosServiceClient.cs:230` HTTP `/invoice`.
    **Nije potvrđeno da PenPOS/ESIR postoji ili radi uz Linux kiosk** — proveriti u F0.

## 4. Izbor GUI-ja

| Opcija | Napor (Avalonia = 1×) | Licenca | Ocena |
|---|---|---|---|
| **A. Avalonia UI** | 1× | MIT, besplatno | **Izabrano.** Najbliže WPF-u, zrelo, X11/Wayland, kiosk |
| B. Avalonia XPF | najmanji UI posao | Business 29.500 € po aplikaciji (+21.250 € ako je „embedded Linux") | Rezerva ako proba A pokaže preveliki trošak; probna licenca preko njihovog tima. `MediaPlayer` po Codex-u nije podržan (nije provereno) |
| D. Uno Platform | ~1–1,3× | Apache-2.0 | Jedina dodatna ozbiljna opcija; WinUI XAML, na Linuxu potvrđen X11, Wayland ne; nema gotovog Linux UI testiranja. Prednost samo uz budući web/mobilni UI |
| C. Blazor + Chromium kiosk | ~1,2–1,6× | MIT/BSD | Ceo UI u HTML/CSS; Photino i Electron.NET slično |
| Qt Quick (QML) | ~1,4–2× | LGPLv3/GPL ili komercijalna | Samo uz embedded strategiju; C# most je pre-release |
| Flutter + .NET preko IPC-a | ~1,5–2× | BSD-3 | Dva jezika; UI mora da obnovi stanje usred plaćanja bez ponovne naplate |
| Slint + .NET preko IPC-a | ~1,5–2,2× | GPLv3 ili komercijalna (verovatno embedded) | Nema C# integracije |
| OpenMaui (nezvanični MAUI za Linux) | ≥1× | MIT | Odbačeno: MAUI XAML dalje od WPF-a (nema `Storyboard`/`EventTrigger`), jedna firma, mali projekat, kiosk i `AutomationId` nedokumentovani |

Zvanični Microsoft MAUI nema Linux.

### Avalonia paketi

| Paket | Cena (bez PDV-a) | Potrebno? |
|---|---|---|
| Free | 0 € | **Da** — ceo framework |
| Plus | 299 €/god. po programeru | Ne (DevTools, Hot Reload — udobnost) |
| Pro | 899 €/god. po programeru | Možda — samo Media Player; On Screen Keyboard nije potreban (imamo `NumericKeypad`) |
| Enterprise | od 21.000 €/god. (3 programera) | Ne, osim ako klijent traži ugovorenu podršku |

## 5. F0 — Proba (4–5 nedelja)

Cilj: na ciljnom Linux uređaju dokazati najrizičnije delove jednim kompletnim tokom i dobiti pouzdanu procenu ostatka.

**Obim**
- Tok **preuzimanja paketa od primaoca** od početka do kraja: šifra → plaćanje (POS) → fiskalni QR → otvaranje vrata →
  povratak na početni ekran
- 1 layout, 1 brend
- Jezgro za taj tok kopirano iz v2 (`0029647` ili novije) u v4, sa navigacijom, fabrikom prikaza, tajmerima i
  neutralnim tipovima slika i boja — struktura koja ostaje i posle probe

**Mora da se dokaže**
- [ ] Kratki i dugi pritisak (touch), Home zaključan tokom plaćanja
- [ ] Animacije (`EventTrigger`/`Storyboard` ekvivalenti), zvuk, QR
- [ ] Gašenje preko SIGTERM sa završnim upisom
- [ ] Kiosk sesija: distribucija (Ubuntu 24.04 LTS ili Debian 12), `cage` (Wayland) ili X11, autologin, rotacija za
      portrait (prikaz **i** touch koordinate), bez screensaver-a, zaključano bez izlaza na desktop
- [ ] Daljinska podrška na Linux uređaju (šta menja RDP/AnyDesk)
- [ ] Jedan serijski kontroler i jedan TCP kontroler (NCU16L/24L) na Linuxu
- [ ] Pravi POS i PenPOS/ESIR ako su dostupni, inače simulatori
- [ ] Build i testovi u CI na Linuxu i Windows-u od prvog dana; verzija Avalonia i backend-a fiksirana
- [ ] Platformski sloj (sekcija 1a) postoji kao struktura; za probu potpuno implementirane samo Linux strane koje
      tok koristi, Windows strane koliko treba za razvoj sa simulatorima

**Van probe**
- Agent kanal (samo pisani dogovor transporta sa Agent stranom), nadogradnja, potpisivanje, E2E, ostali tokovi,
  ostali layouti i brendovi

**Ishod**
- Spisak: šta je radilo direktno, šta zaobilazno, šta nije rešeno besplatnim paketom
- Procena ostatka na osnovu stvarnog tempa
- Odluka: nastavak (A), dokupljivanje paketa, ili XPF probna licenca — sa konkretnim razlogom

## 6. Nastavak posle odluke klijenta

### F1 — Ostatak jezgra (2–3 nedelje)
- [ ] Svi tokovi: kurirsko preuzimanje i ostavljanje, ostavljanje pošiljaoca, servisni režim
- [ ] Čisti workflow testovi kopirani i zeleni na Linuxu

### F2 — Platformski servisi i Agent (2–3 nedelje)
- [ ] Stavke 1–5 i 8 iz sekcije 3 u celini
- [ ] Agent kanal po dogovoru; **paralelno** Linux ServiceAgent (drugi repo, nije u ovoj proceni)

### F3 — UI (5–7 nedelja kiosk + 2–3 nedelje alati)
- [ ] Sve stranice × 6 layouta, svi brendovi
- [ ] LockerSimulator, ServerSimulator (`reserve-urlacl.ps1` nije potreban)
- [ ] Configurator (stavka 10); Technician na kraju (seli se u Locker Station Services)

### F4 — Testovi (3–4 nedelje)
- [ ] Odvojeno: čisti testovi, platformski IPC, stvarna kiosk sesija
- [ ] E2E: Avalonia Headless za UI logiku + AT-SPI ili sopstveni hook za pravu sesiju; `AutomationId` se prenosi
- [ ] Testovi gašenja preko SIGTERM
- [ ] POS simulator: com0com → `socat` virtuelni serijski par

### F5 — Isporuka i operativa (3–4 nedelje)
- [ ] Samostalan `linux-x64` build, `.deb` ili tarball, systemd servis, korisnik programa
- [ ] Potpis i manifest izdanja sa oznakom OS-a i arhitekture (`InnoSetup/generate-release-info.ps1:87`,`:103`) —
      dogovor sa Agent/server stranom za mešanu flotu
- [ ] Nadogradnja i vraćanje (ekvivalent `LockerStation.snapshot`) koje čuva redove potvrda i stanje
- [ ] Logovanje (Serilog putanje, Seq)

### F6 — Pilot (2–3 nedelje)
- [ ] Sva četiri tipa kontrolera, POS, PenPOS/ESIR, barkod, gašenje, nestanak struje
- [ ] Pun E2E, višednevni burn-in

### F7 — Prelazak Windows flote na v4 (3–5 nedelja, posle uspešnog Linux pilota)
- [ ] Windows implementacije platformskog sloja kompletne (kanal ka Agentu sa SID proverom, kraj sesije)
- [ ] Windows regresija: proveriti da li postojeći FlaUI harness iz v2 radi nad v4 (Avalonia podržava UI Automation
      i `AutomationId` — neprovereno); ako radi, uporedni E2E v2 vs v4
- [ ] Instaler: InnoSetup iz v2 prilagođen v4; nadogradnja v2 → v4 na postojećim instalacijama (stanje, redovi
      potvrda, konfiguracija)
- [ ] Pilot na jednom Windows paketomatu, zatim postepeno ostali
- [ ] v2 prelazi na „samo ispravke grešaka"

## 7. Procena

Jedan iskusan programer koji zna projekat; čovek-nedelje odvojene od kalendara.

| Deo | Čovek-nedelje |
|---|---|
| F0 proba | 4–5 |
| F1–F6 (Linux u produkciji) | 19–27 |
| **Ukupno do Linux produkcije** | **~23–32** |
| Kalendarski (čekanje hardvera, Agent strane, pilot) | **~6–8 meseci** |
| F7 Windows flota na v4 | +3–5 (kalendarski +1–2 meseca, zavisi od tempa prelaska flote) |

- **Nije uključen ServiceAgent za Linux** — uslov za produkciju.
- Ubrzanje uz AI alate nije ugrađeno u procenu.
- Za poređenje: XPF put po Codex-u 14–20 čovek-nedelja (4–6 meseci) + licenca, ako XPF proba prođe.

## 8. Rizici

1. **ServiceAgent** — bez njega nema daljinskog upravljanja ni nadogradnje.
2. **PenPOS/ESIR na Linuxu** — nepotvrđena zavisnost fiskalizacije.
3. **Dva koda dok traje prelaz** — ispravke se prenose ručno (`PORTED_FROM.md`) do F7; što ranije F7, to kraće.
4. **Kiosk na Linuxu** — touch, rotacija, zaključavanje, daljinska podrška.
5. Serijski uređaji — drajveri USB adaptera, ponovno povezivanje.
6. **Gašenje na Windows-u u Avaloniji** — redosled iz v2 (51f479b) mora da se dokaže iznova.

## 9. Review (Codex, 9.11.2026)

Prihvaćeno svih 15 nalaza; ništa nije odbačeno. Nalazi 6, 8 i 14 se oslanjaju na spoljnu dokumentaciju koja nije
posebno proverena.

| # | Nalaz | Gde u planu |
|---|---|---|
| 1 | Izdvajanje jezgra teže od 2 nedelje (`UserControl` u `IWorkflowHost`, WPF tipovi u ViewModel-ima) | sekcija 2, F0 |
| 2 | ECR registry/`hwid` nije bloker (Ingenico, V3) | sekcija 3.9 |
| 3 | `SerialClient` već čita preko `ReadAtLeastAsync` | sekcija 3.8 |
| 4 | Configurator `ToLower()` i COM1–COM25 | sekcija 3.10 |
| 5 | Putanje i stanje pre UI porta | sekcija 3.5 |
| 6 | .NET 9 kraj podrške, `git.exe` u build-u | sekcija 1, 2, F0 |
| 7 | Storyboard ~8, ne 51; `BoxCubeAnimation` nije na `dev`; `LanguageManager` neutralan | sekcija 2 |
| 8 | Prednost Avalonie nad XPF nije dokazana | sekcija 4, ishod F0 |
| 9 | Pogrešne pretpostavke o testovima | sekcija 2, F4 |
| 10 | Proba mora da pokrije touch, Home, kiosk, daljinsku podršku | F0 |
| 11 | Dogovor sa Agentom od početka | sekcija 3.1, F0, F2 |
| 12 | Potpis i manifest za mešanu flotu | F5 |
| 13 | Fiskalizacija nije bila pokrivena | sekcija 3.11, F0, F6 |
| 14 | Opseg named mutex-a na Unix-u | sekcija 3.4 |
| 15 | Zbir faza nije bio tačan | sekcija 7 |

Dodatno pitanje o GUI opcijama: Codex dodao Uno, Photino, Electron.NET, Qt, Flutter, Slint i strategiju „WPF ostaje
na Windows-u"; osnovna preporuka nepromenjena. OpenMaui razmotren naknadno i odbačen (sekcija 4).
