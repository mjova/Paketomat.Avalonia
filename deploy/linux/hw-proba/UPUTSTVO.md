# Program na Debian mašini sa pravim uređajima

Pokretanje programa kioska (v4) sa desktopa, bez instalacije i bez kiosk režima. Uređaji su pravi: POS, barkod skener i
kontroler vrata na serijskim portovima (RS232/RS485). Server je test server.

U folderu: `toppos-kiosk-<oznaka>-linux-x64.tar.gz` (program), `pokreni-probu.sh` i ovo uputstvo. U isti folder staviti
i `app-configuration.json` sa test paketomata (v2).

Pri pokretanju program proverava POS (čita TID). **Plaćanje odobreno na produkcionom POS terminalu je stvarna naplata**,
i kad program radi sa test serverom.

## Brzo: sve jednom komandom

Iz terminala na desktopu, u folderu sa USB-a, kao običan korisnik (skripta sama traži `sudo` lozinku):
```
bash pokreni-probu.sh
```

Pri prvom pokretanju, uz internet, skripta:
- instalira biblioteke, dodaje korisnika u grupu `dialout` i zaustavlja ModemManager (korak 1 ispod);
- instalira RustDesk (poslednje izdanje sa GitHub-a) sa stalnom lozinkom. Na Enter se lozinka sama napravi; ID i
  lozinka se ispisuju na ekranu, pa ih treba **slikati**, jer se nigde ne čuvaju. GNOME prijavu prebacuje na X11,
  što važi od sledeće prijave; tada veza ne traži potvrdu na mašini;
- raspakuje program u `~/toppos/toppos-kiosk-.../app` i kopira konfiguraciju u `~/toppos/konfiguracija`.

Pri svakom pokretanju:
- prikazuje pronađene serijske portove pod brojevima i za kontroler vrata, POS i skener pita broj (Enter = zadrži, 0 = bez porta; POS bez porta se isključuje).
  Uz port na ploči (`ttyS`) piše adresa i IRQ (npr. `adresa 0x2F8 (obično COM2), IRQ 3`), za poređenje sa BIOS-om i
  oznakama na ploči; uz port koji je već dodeljen piše kom uređaju. Isti port se ne može dati dvama uređajima;
- pokreće program. Odjava zbog grupe `dialout` nije potrebna.

Konfiguracija, stanje i logovi su u stalnim folderima `~/toppos/konfiguracija`, `~/toppos/stanje` i `~/toppos/logovi`,
pa ih nov paket ne menja. Logovi za USB (bez ECR loga): `bash pokreni-probu.sh logovi`; arhiva nastaje pored skripte.

Ostatak uputstva su isti koraci, ručno (tada je sve u folderu `app`).

## 1. Priprema sistema (jednom, traži internet)

```
sudo apt-get update
sudo apt-get install -y libicu72 libvlc5 vlc-plugin-base libfontconfig1 xwayland \
  libx11-6 libice6 libsm6 libxcursor1 libxext6 libxfixes3 libxi6 libxrandr2 libgl1
sudo usermod -aG dialout $USER
systemctl is-active ModemManager
```

- `libicu72` važi za Debian 12; na Ubuntu 24.04 je `libicu74`.
- Grupa `dialout` daje pristup serijskim portovima i važi tek posle **odjave i ponovne prijave**.
- ModemManager šalje AT komande svakom novom serijskom uređaju, pa ometa POS i skener. Ako poslednja komanda ispiše
  `active`, pre probe ga zaustaviti sa `sudo systemctl stop ModemManager`, a posle probe vratiti sa
  `sudo systemctl start ModemManager`. Pri sledećem boot-u se ionako vraća kakav je bio.

## 2. Program

```
mkdir -p ~/toppos && cd ~/toppos
tar xzf /putanja/toppos-kiosk-*-linux-x64.tar.gz
cd toppos-kiosk-*-linux-x64
cp serilog.json app/
cp /putanja/app-configuration.json app/
```

## 3. Serijski portovi

```
ls -l /dev/serial/by-id/
```

Svaki red je jedan USB-serijski uređaj sa stalnim imenom (npr. `usb-FTDI_..._A10K3X-if00-port0 -> ../../ttyUSB0`). Koji
je koji: izvaditi jedan uređaj, ponoviti komandu — red koji nestane je taj uređaj. Port na samoj ploči (bez USB-a) je
`/dev/ttyS0`, `/dev/ttyS1`...

U `app/app-configuration.json` umesto `COMx` upisati pune putanje (`/dev/serial/by-id/...` ili `/dev/ttySx`):
- `Locker.ComPort.PortName`: kontroler vrata (`Locker.ControllerType` ostaje kao na paketomatu, `Serial` ili
  `SerialDailyExpress`);
- `PosTerminal.ComPort.PortName`: POS. `PosTerminal.TerminalInstalled` mora biti `true`; inače program POS ne
  koristi ni ne proverava;
- `BarcodeScanner.ComPort.PortName`: skener.

Ako konfiguracija ima sekciju `ScreenResolution`, program radi u prozoru te veličine; bez nje zauzima ceo ekran.

## 4. Pokretanje

```
cd ~/toppos/toppos-kiosk-*-linux-x64/app
chmod u+x ./TopPos.Kiosk
./TopPos.Kiosk
```

Program zauzima ceo ekran (do terminala: `Alt+Tab`). Gašenje dok je na Start stranici, a ne tokom plaćanja ili
otvaranja vrata: `Ctrl+C` u terminalu ili `Alt+F4`.

## 5. Gde je šta

- Log: `app/Logs/log-*.txt`. Na početku su podaci o portovima, zatim otvaranje svakog uređaja i greške.
- Stanje: `app/status.lock`, gde `"CleanShutdown": true` znači uredno gašenje.
- Pri odlasku na USB kopirati samo `app/Logs/log-*.txt` (i `app/status.lock`). Podfolder `app/Logs/ECR` (log POS
  biblioteke) ostaje na mašini, jer može da sadrži podatke kartice.

## Ako nešto ne radi

- **Port se ne otvara** („Permission denied“): korisnik nije u grupi `dialout`. Proveriti sa `id`, pa se odjaviti i
  prijaviti.
- **Port ne postoji:** proveriti putanju sa `ls -l /dev/serial/by-id/`. Na Ubuntu-u paket `brltty` ume da preuzme
  USB-serijski adapter; pomaže `sudo apt remove brltty`.
- **Program se ne pokreće, greška `DllNotFoundException`:** fali neka biblioteka iz koraka 1.
- **Program odmah pada sa greškom o X11 prikazu (`XOpenDisplay`):** pokrenuti iz terminala na desktopu, ne preko SSH-a.
  Na Wayland desktopu treba `xwayland` iz koraka 1.
