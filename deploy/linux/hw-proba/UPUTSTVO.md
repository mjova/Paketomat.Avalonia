# Program na Debian mašini sa pravim uređajima

Pokretanje programa kioska (v4) sa desktopa, bez instalacije i bez kiosk režima. Uređaji su pravi: POS, barkod skener i
kontroler vrata na serijskim portovima (RS232/RS485). Server je test server.

U folderu: `toppos-kiosk-<oznaka>-linux-x64.tar.gz` (program) i ovo uputstvo. Poneti još `app-configuration.json` sa
test paketomata (v2).

Pri pokretanju program proverava POS (čita TID). **Plaćanje odobreno na produkcionom POS terminalu je stvarna naplata**,
i kad program radi sa test serverom.

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
