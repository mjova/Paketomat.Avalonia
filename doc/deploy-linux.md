# Isporuka na Linux (F0b, priprema)

Kiosk radi kao systemd servis `toppos-kiosk`: kiosk kompozitor **cage** na `tty1` (servis pre starta prelazi na njega i
gasi getty tog VT-a), kiosk unutra kroz XWayland (Avalonia na Linuxu crta kroz X11). Ekran i ulaz daje **seatd**. Bez desktopa,
bez prozora i bez prelaska na drugi VT. Podržano: Debian 12 i Ubuntu 24.04 (apt).

Fajlovi u `deploy/linux/`:
- `publish.sh` — pravi paket;
- `install.sh` — instalacija;
- `boot-mode.sh` — bira šta se diže pri boot-u, kiosk ili desktop;
- `toppos-kiosk.service` — servis;
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
- instalira zavisnosti: `cage`, `xwayland`, `seatd`, `kbd` (`chvt`), `libicuNN`, `libvlc5`, `vlc-plugin-base`, `libfontconfig1`, i
  uključuje `seatd.service`;
- pravi sistemskog korisnika `toppos` sa grupama `dialout` (serijski uređaji), `audio` (zvuk preko ALSA) i grupom
  seatd-a (`seatd -g`, na Debianu `video`: ekran i touch);
- instalirano izdanje ne prepisuje. Novo izdanje kopira u privremeni folder pa ga preimenuje, a `current` menja atomski,
  pa prekinuta instalacija ne ostavlja `current` na nepotpunom izdanju;
- postojeću konfiguraciju ne menja. `app-configuration.json` upisuje samo uz `--config`, a `serilog.json` samo ako ga nema;
- maskira `sleep`, `suspend`, `hibernate` i `hybrid-sleep`. Na Debian VM-u je KDE uspavao gosta usred rada kioska;
- bez `--kiosk-boot` desktop ostaje, a servis se ručno pokreće sa `sudo systemctl start toppos-kiosk` (prelazi na
  tty1). Sa `--kiosk-boot` poziva `boot-mode.sh kiosk`.

**Proba pored desktopa** (desktop na tty1, `loginctl show-session <id> -p VTNr`): kiosk ide na tty2, a getty na tty2 se
maskira, da ga logind ne pokrene pri prelasku i tako ne ugasi kiosk (`Conflicts`). Maske i drop-in su u `/run`, pa
zajedno nestaju pri restartu:
```
sudo systemctl mask --runtime --now getty@tty2.service autovt@tty2.service
sudo mkdir -p /run/systemd/system/toppos-kiosk.service.d
printf '[Service]\nExecStartPre=\nExecStartPre=+/usr/bin/chvt 2\n' | sudo tee /run/systemd/system/toppos-kiosk.service.d/vt.conf
sudo systemctl daemon-reload
systemctl show toppos-kiosk -p ExecStartPre    # mora biti chvt 2
sudo systemctl start toppos-kiosk
```
Povratak: `sudo systemctl stop toppos-kiosk`, `sudo chvt 1`, `sudo rm -r /run/systemd/system/toppos-kiosk.service.d`,
`sudo systemctl daemon-reload`, `sudo systemctl unmask --runtime getty@tty2.service autovt@tty2.service`. Pošto cage ne
dozvoljava prelazak na drugi VT, skripta probe mora sama da vrati ekran (trap i vremenska granica).

Na Debian VM-u (VMware, `vmwgfx`) KDE posle povratka ostaje crn. KWin javlja „Page flip failed: No space left on device“:
cage je prikaz držao preko seatd-a, a KWin se vraća preko logind-a. Pomaže prelazak na drugi VT i nazad, odjava ili
restart. Na uređaju desktop ne radi pored kioska, pa ovo važi samo za probu pored desktopa.

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
prikaz dok kiosk još radi. Pri gašenju sistema servis staje pre seatd-a (`After=seatd.service`).

Kad kiosk padne, cage izlazi i servis se ponovo pokreće posle 2 s (`Restart=always`).

**Zašto seatd, a ne logind sesija (`PAMName`).** Proba na Debian VM-u 10.11.2026 sa `PAMName`:
- logind premešta sve procese servisa u svoj `session-N.scope`;
- kad se taj scope gasi, kao pri gašenju sistema, paralelno sa servisom, svi procesi odmah dobijaju SIGTERM i SIGHUP;
- kiosk je od SIGHUP-a umro bez završnog upisa: `CleanShutdown=false` u 3 od 3 pokušaja;
- `KillMode` i SIGKILL posle `TimeoutStopSec` nisu pogađali procese u scope-u.

Sa seatd-om svi procesi ostaju u cgroup-u servisa. Kiosk od tada SIGHUP obrađuje kao zahtev za gašenje.

Proba sa seatd-om na istom VM-u, 10.11.2026 (`844c542`):

| Slučaj | Ishod |
|---|---|
| Start | Start stranica za ~7 s |
| `stop` | ~1 s, uredno (`CleanShutdown=true`) |
| `restart` | ~1 s, uredno |
| `stop` servisa i seatd-a jednom komandom, 3 puta | uredno svaki put; seatd staje tek posle kioska |
| Zamrznut kiosk | SIGKILL svim procesima posle 30 s, nijedan ne preživi |
| `kill -9` kioska | novi Start za ~10 s |

Bez korisničke sesije nema D-Bus-a, pa Avalonia beleži da AT-SPI i D-Bus nisu dostupni; kiosku to ne smeta. AT-SPI za
E2E radi u desktop sesiji (`doc/e2e.md`).

## Otvoreno (uređaj)

- Rotacija prikaza i touch-a: cage prati transformaciju izlaza; proveriti sa pravim touch-om kroz XWayland, kao i kratak i
  dug pritisak.
- Ako cage ili XWayland ne odgovaraju na uređaju, rezerva je X11 bez kompozitora: `xinit` + Xorg sa `-nocursor`, a
  rotacija preko `xrandr` i `xinput` matrice. Struktura servisa ostaje ista, menja se samo komanda u `kiosk-session.sh`.
- Serijski uređaji u `app-configuration.json` preko `/dev/serial/by-id/...`.
- ECR (Payten) čita `ecrcfg.xml`/`ecr.cfg` iz foldera programa (`/opt/.../current`, samo za čitanje) — odluka uz pravi
  POS.
- Daljinska podrška (zamena za RDP/AnyDesk).
- Zvuk: LibVLC preko ALSA (grupa `audio`, bez PipeWire-a i korisničke sesije) — proveriti izlaz na uređaju.
