# Isporuka na Linux (F0b, priprema)

Kiosk radi kao systemd servis `toppos-kiosk`: kiosk kompozitor **cage** na `tty1`, kiosk unutra kroz XWayland (Avalonia
na Linuxu crta kroz X11). Bez desktopa, bez prozora i bez prelaska na drugi VT. Podržano: Debian 12 i Ubuntu 24.04 (apt).

Fajlovi u `deploy/linux/`:
- `publish.sh` — pravi paket;
- `install.sh` — instalacija;
- `boot-mode.sh` — bira šta se diže pri boot-u, kiosk ili desktop;
- `toppos-kiosk.service` i `toppos-kiosk.pam` — servis i njegova sesija;
- `kiosk-session.sh` — glavni proces servisa (cage + kiosk, uredno gašenje).

## Paket

```
deploy/linux/publish.sh [--rid linux-x64|linux-arm64] [--version 4.0.0]
```

Paket je Release i self-contained (.NET runtime je unutra), bez trimovanja. Ishod je
`artifacts/toppos-kiosk-<oznaka>-<rid>.tar.gz`. Oznaka ima oblik `<verzija ili dev>-<UTC vreme>-<commit>[-dirty]`. Ona je
ujedno ime foldera izdanja na uređaju, pa je jedinstvena za svaki build. Oznaka se upisuje u `paketomat.ver` i u zaglavlje
loga. `app-configuration.json` ne ulazi u paket. Skripta radi na Linuxu i iz Git Bash-a na Windows-u.

## Instalacija

```
tar xzf toppos-kiosk-<oznaka>-linux-x64.tar.gz
cd toppos-kiosk-<oznaka>-linux-x64
sudo ./install.sh --config /putanja/app-configuration.json [--kiosk-boot]
```

| Šta | Gde |
|---|---|
| Program (po izdanju) | `/opt/toppos-kiosk/releases/<oznaka>`, aktivno: link `/opt/toppos-kiosk/current` |
| Konfiguracija (`app-configuration.json`, `serilog.json`) | `/etc/toppos-kiosk` (`ConfigurationDirectory`, root, samo za čitanje) |
| Stanje (`status.lock`, redovi potvrda, dnevnik operacija) | `/var/lib/toppos-kiosk` (`StateDirectory`) |
| Logovi (kiosk, `ECR/`) | `/var/log/toppos-kiosk` (`LogsDirectory`); izlaz procesa u `journalctl -u toppos-kiosk` |

`install.sh`:
- instalira zavisnosti: `cage`, `xwayland`, `libicuNN`, `libvlc5`, `vlc-plugin-base`, `libfontconfig1`, `dbus-user-session`;
- pravi sistemskog korisnika `toppos` u grupi `dialout`, zbog serijskih uređaja. Ekran, touch i zvuk daje logind sesija
  servisa (`PAMName=toppos-kiosk`), pa grupe `video`, `input` i `audio` nisu potrebne;
- instalirano izdanje ne prepisuje. Novo izdanje kopira u privremeni folder pa ga preimenuje, a `current` menja atomski,
  pa prekinuta instalacija ne ostavlja `current` na nepotpunom izdanju;
- postojeću konfiguraciju ne menja. `app-configuration.json` upisuje samo uz `--config`, a `serilog.json` samo ako ga nema;
- maskira `sleep`, `suspend`, `hibernate` i `hybrid-sleep`. Na Debian VM-u je KDE uspavao gosta usred rada kioska;
- bez `--kiosk-boot` desktop ostaje, a servis se pokreće ručno: `sudo systemctl start toppos-kiosk` (prebacuje ekran na
  tty1). Sa `--kiosk-boot` poziva `boot-mode.sh kiosk`.

Vraćanje na prethodno izdanje: `sudo ln -sfn /opt/toppos-kiosk/releases/<staro> /opt/toppos-kiosk/current` i
`sudo systemctl restart toppos-kiosk`. Čuvanje stanja pri nadogradnji i potpis paketa dolaze u F5.

## Kiosk ili desktop pri boot-u

```
sudo /opt/toppos-kiosk/boot-mode.sh kiosk     # podrazumevani cilj multi-user.target + servis toppos-kiosk
sudo /opt/toppos-kiosk/boot-mode.sh desktop   # graphical.target (display manager), servis isključen
```

Promena važi od sledećeg boot-a; sa `--now` važi odmah (`systemctl isolate`). Menjaju se samo podrazumevani cilj i
uključenost servisa. Display manager (desktop) ostaje podešen kao pre, samo se u režimu kiosk ne diže, jer se
`graphical.target` ne dostiže.

Kad radi kiosk, do terminala se dolazi preko SSH-a ili ovako (cage ne dozvoljava prelazak na drugi VT):
1. Restart uređaja, pa u GRUB meniju taster `e`.
2. Na kraj reda koji počinje sa `linux` dodati `systemd.mask=toppos-kiosk.service systemd.unit=graphical.target`, pa
   `Ctrl+X`.
3. Sistem se diže u desktop bez kioska, samo za taj boot. Na uređaju bez desktopa se diže tekstualna prijava.
4. Posle prijave: `sudo /opt/toppos-kiosk/boot-mode.sh desktop`.

Ako GRUB meni ne stoji na ekranu, pri startu treba držati `Shift` (BIOS) ili pritiskati `Esc` (UEFI).

**Proba na VM-u:** pre `--kiosk-boot` napraviti snapshot (VMware: VM → Snapshot → Take Snapshot). Vraćanje snapshot-a
poništava sve.

## Gašenje

Glavni proces servisa je `kiosk-session.sh`, koji pokreće cage sa kioskom. Servis ima `KillMode=mixed`, pa
`systemctl stop` i `restart` šalju SIGTERM samo toj skripti. Gašenje ide ovim redom:
1. Skripta prosleđuje SIGTERM kiosku i čeka ga.
2. Kiosk zatvara kontroler i upisuje završno stanje u `status.lock`.
3. Kad kiosk izađe, cage izlazi sam.

Granica je `TimeoutStopSec=30`; posle nje systemd šalje SIGKILL svima. Kad bi signal stigao svima odjednom, cage bi ugasio
prikaz dok kiosk još radi. `ExecStop` se ne koristi: TTY podešavanja servisa (`TTYVHangup`) važe i za njega i prekinula bi
sesiju pre gašenja kioska.

Kad kiosk padne, cage izlazi i servis se ponovo pokreće posle 2 s (`Restart=always`).

**Otvoreno — gašenje celog sistema:** `PAMName` premešta procese u logind sesiju (`session-N.scope`). Pri gašenju
sistema systemd tu sesiju gasi paralelno sa servisom, pa cage može da izgubi prikaz pre nego što kiosk završi. Proverava
se na VM-u (`systemctl reboot` uz proveru `CleanShutdown` u `status.lock`). Ako kiosk ne podnosi gubitak prikaza tokom
gašenja, rešenje je sesija bez logind-a (seatd) ili gašenje kioska koje ne zavisi od prikaza.

## Otvoreno (uređaj)

- Rotacija prikaza i touch-a: cage prati transformaciju izlaza; proveriti sa pravim touch-om kroz XWayland, kao i kratak i
  dug pritisak.
- Ako cage ili XWayland ne odgovaraju na uređaju, rezerva je X11 bez kompozitora: `xinit` + Xorg sa `-nocursor`, a
  rotacija preko `xrandr` i `xinput` matrice. Struktura servisa ostaje ista, menja se samo komanda u `kiosk-session.sh`.
- Serijski uređaji u `app-configuration.json` preko `/dev/serial/by-id/...`.
- ECR (Payten) čita `ecrcfg.xml`/`ecr.cfg` iz foldera programa (`/opt/.../current`, samo za čitanje) — odluka uz pravi
  POS.
- Daljinska podrška (zamena za RDP/AnyDesk).
- Zvuk: LibVLC u logind sesiji bez desktopa (PipeWire korisničke sesije ili ALSA direktno).
