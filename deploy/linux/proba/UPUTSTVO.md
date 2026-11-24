# Proba programa na Debian mašini (POS i skener)

Program kioska (v4) se pokreće direktno sa desktopa, bez kiosk režima, bez instalacije i bez promene boot-a. Pravi su
POS terminal i barkod skener. Server i kontroler vrata zamenjuju lokalni simulatori, pa mreža posle pripreme nije
potrebna. Cilj probe: ekran, touch, zvuk, POS (prisustvo i plaćanje) i skener.

## Šta je u folderu

| Fajl | Šta je |
|---|---|
| `toppos-kiosk-<oznaka>-linux-x64.tar.gz` | program kioska (raspakuje ga `pokreni-kiosk.sh`) |
| `toppos-simulators-<commit>-linux-x64.tar.gz` | simulatori servera i kontrolera vrata (raspakuje ga `pokreni-simulatore.sh`) |
| `kiosk-proba.json` | konfiguracija programa — **u nju se upisuju POS i skener** |
| `server.json`, `locker.json`, `dodaj-pakete.sh` | podešavanja simulatora i paketi za probu |
| `priprema.sh` | jednom, sa `sudo`: biblioteke, grupa `dialout`, gašenje ModemManager-a |
| `serijski-portovi.sh` | spisak serijskih portova (koji je POS, koji skener) |
| `pokreni-simulatore.sh`, `pokreni-kiosk.sh` | pokretanje (svaki u svom terminalu) |
| `zatvori-vrata.sh` | „zatvaranje“ vrata u simulatoru posle preuzimanja |
| `prikupi-logove.sh` | svi logovi i podaci o mašini u jednom fajlu |

## Poneti

- Ceo ovaj folder (USB).
- Sa ispravnog Windows paketomata (v2), iz njegovog `app-configuration.json`, prepisati **samo** `PosTerminal.ProductKey`,
  `PosTerminal.ComPort` i `BarcodeScanner.ComPort` (korak 4), npr. u tekstualni fajl ili odmah u `kiosk-proba.json`. Ceo
  fajl ne nositi: u njemu su i lozinke produkcionih servisa.
- Kablove za POS i skener. Telefon sa aplikacijom za prikaz barkoda (za probu skenera).
- Karticu za probu plaćanja: **POS je pravi, pa je i naplata prava** (paket 345678, 10 RSD).

## 1. Kopiranje

Folder kopirati na mašinu, npr. u `~/toppos-proba`. Sve dalje komande su iz tog foldera (`cd ~/toppos-proba`), u
terminalu na desktopu. USB (FAT/exFAT) ne čuva pravo izvršavanja, pa prvo:
```
cd ~/toppos-proba
chmod u+x ./*.sh
```

## 2. Priprema (jednom, traži internet)

```
sudo ./priprema.sh
```

Instalira biblioteke (ICU, LibVLC, fontconfig, curl, XWayland), dodaje korisnika u grupu `dialout` (serijski portovi) i
gasi ModemManager, koji bi slao AT komande POS-u i skeneru. **Posle toga se odjaviti i ponovo prijaviti.**

## 3. Serijski portovi

Priključiti POS i skener, pa:
```
./serijski-portovi.sh
```

Ispisuje stalna imena, npr. `/dev/serial/by-id/usb-Ingenico_...-if00 -> /dev/ttyACM0`. Koje je ime koji uređaj: izvaditi
jedan uređaj i pokrenuti skriptu ponovo — red koji nestane je taj uređaj.

## 4. Konfiguracija

U `kiosk-proba.json` se iz v2 prenose **samo** ova polja (ostalo u sekcijama ostaje kako je):
1. `PosTerminal.ProductKey` (`KeyE`, `KeyW`) i `PosTerminal.ComPort` (brzina, parnost...), a u njemu `PortName` staviti
   na stalno ime POS-a (`/dev/serial/by-id/...`, umesto `COMx`).
2. `BarcodeScanner.ComPort`, sa `PortName` = stalno ime skenera.
3. Ostalo ne menjati. `PosTerminal.InputV2.AmountCurrency` mora ostati `941` (dinar), jer ekran prikazuje RSD. Adrese
   su lokalni simulatori, paketomat 111, PenPOS isključen.

Konfiguracija se kopira u program pri svakom pokretanju, pa se menja samo ovde. Ako POS ne radi, za drugi pokušaj se može
uključiti log POS biblioteke (`PosTerminal.Logging.Enabled` i `Detailed` na `true`, log u `logovi-kiosk/ECR`). Taj log
može da sadrži podatke kartice, pa posle probe ostaje na mašini.

## 5. Pokretanje

Terminal 1:
```
./pokreni-simulatore.sh
```
Mora se pojaviti red `>>> Paketi za probu: ...`. Simulatori rade dok je terminal otvoren (gašenje: `Ctrl+C`).

Terminal 2:
```
./pokreni-kiosk.sh
```
Program zauzima ceo ekran; do terminala se dolazi sa `Alt+Tab`. **Gasiti samo dok je na Start stranici**, ne tokom
plaćanja ili otvaranja vrata: `Ctrl+C` u terminalu 2 ili `Alt+F4` na programu. Ponovno pokretanje: ista komanda.

Terminal 3 služi za `./zatvori-vrata.sh` (sledeći korak).

## 6. Šta proveriti

Paketi u simulatoru (telefon: bilo koji broj):

| Šifra | Vrata | Napomena |
|---|---|---|
| 123456 | 5 | bez plaćanja |
| 234567 | 7 | bez plaćanja |
| 345678 | 9 | otkup **10 RSD — pravo plaćanje karticom** |

**Posle otvaranja vrata** kiosk čeka da kupac zatvori vrata. U simulatoru se to radi iz terminala 3:
`./zatvori-vrata.sh 5` (odnosno 7 ili 9). Bez toga kiosk posle isteka javlja grešku vrata.

Preuzet paket se vraća tek posle ponovnog pokretanja simulatora: prvo ugasiti program (sa Start stranice), pa u
terminalu 1 `Ctrl+C` i ponovo `./pokreni-simulatore.sh`, pa program (skripta odbija start simulatora dok program radi,
jer bi neposlata potvrda naplate mogla da se izgubi). Dok simulator radi,
plaćen paket ostaje plaćen, pa se odustajanje (Cancel na POS-u) i istek bez kartice probaju **pre** odobrene naplate.
**Ponovno pokretanje simulatora briše i evidenciju uplate:** paket 345678 je opet neplaćen, a novo odobrenje na POS-u
ponovo tereti karticu (prethodna naplata ostaje).

Zapisati šta ne radi (uz fotografiju ekrana i vreme):
- **Start:** da li se program diže bez poruke o grešci POS-a, skenera ili kontrolera; koliko traje.
- **Ekran i touch:** slika na celom ekranu, pravilno okrenuta; dodir pogađa dugme ispod prsta, i u uglovima.
- **Preuzimanje bez plaćanja:** 123456 → telefon → vrata 5 → `./zatvori-vrata.sh 5` → povratak na Start.
- **Plaćanje:** 345678 → telefon → ekran plaćanja. Prvo Cancel na POS-u, pa istek bez kartice, pa kartica →
  odobreno → vrata 9 → `./zatvori-vrata.sh 9`.
- **Skener:** na Start stranici skenirati barkod sa telefona; u logu (`logovi-kiosk/`) se vidi primljen barkod. Barkod sa
  tekstom `9999999` (servisna šifra iz konfiguracije) otvara servisna vrata 3 (`./zatvori-vrata.sh 3`).
- **Ostalo:** pogrešna šifra (npr. 111111) → poruka greške; Home vraća na Start (tokom plaćanja je ugašen); promena
  jezika; zvuk klika i govorne poruke; istek: na unosu šifre ništa ne dirati ~20 s → povratak na Start.
- **Prekid uređaja:** dok je program na Start stranici izvaditi i vratiti kabl skenera, pa POS-a — da li se program
  sam oporavi.

Ako se ishod plaćanja ne zna (program ugašen ili pao tokom plaćanja), proveriti promet na POS-u pre novog pokušaja.

## 7. Logovi — obavezno pre odlaska

Ugasiti program (sa Start stranice), pa:
```
./prikupi-logove.sh
```

Nastaje `logovi-<mašina>-<vreme>.tar.gz` u folderu; kopirati ga na USB i doneti uz beleške i fotografije. Ključeva POS-a
u njemu nema, a ECR log nije uključen.

## Ako nešto ne radi

- **„nije u grupi dialout“ ili port se ne otvara:** `sudo ./priprema.sh`, pa odjava i prijava; `./serijski-portovi.sh`.
- **„Nema DISPLAY“:** pokrenuti iz terminala na desktopu; na Wayland desktopu je potreban XWayland (`priprema.sh` ga
  instalira), ili pri prijavi izabrati X11 sesiju.
- **Program ne startuje:** poruka je u terminalu 2 i u `logovi-kiosk/`.
- **Greška kontrolera ili servera:** da li radi terminal 1 (simulatori).
- **POS se ne javlja:** `PortName` i brzina iz v2; zatim log POS biblioteke (korak 4).

## Posle probe

Ništa nije instalirano osim paketa iz koraka 2. Brisanje: folder `~/toppos-proba`. ModemManager se vraća sa
`sudo systemctl enable --now ModemManager`.
