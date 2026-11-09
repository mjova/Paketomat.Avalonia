## RS485 Protokol - ControllerDriverRS485

Implementiran u:
- `Dispenser.Driver\Dispenser.Driver.Controller.RS485\ControllerDriverRS485.cs`
- `Dispenser.Driver\Dispenser.Driver.Controller.RS485\RS485PortController.cs`
- Interface: `IControllerDriver.cs`

### Struktura poruke (fiksno 8 bajtova)

```
[0] = 0x02  (STX - Start of Text)
[1] = Adresa kontrolera
[2] = Komandni kod
[3] = Parametar (adresa brave / 0x00 / 0xFF)
[4] = Data bajt 1
[5] = Data bajt 2
[6] = Data bajt 3
[7] = 0x03  (ETX - End of Text)
```

### Validacija odgovora (MessageCRC metoda)

- Odgovor mora imati minimum 3 bajta
- response[0] mora biti 0x06 (ACK)
- Mora sadržati 0x03 (ETX)
- response[1] i response[2] moraju da echo-uju adresu i komandni kod
- **Nema CRC/checksum** - validacija samo na osnovu strukture frejma

### Komandni kodovi

| Funkcija | Kod (hex) | Parametar [3] | Odgovor [4] |
|---|---|---|---|
| GetVersion | 0xA8 | 0x00 | 4 bajta verzije |
| CheckLockStatus | 0xBB | Adresa brave | Status brave |
| GetAllLocksStatus | 0xDD | 0xFF | Status svih brava |
| OpenLock | 0xAA | Adresa brave | Status brave |
| GetAllIRSensorStatus | 0xEE | 0xFF | Status svih senzora |
| Relay ON (pos 0) | 0x1F | 0x00 | 0xFF = uspeh |
| Relay OFF (pos 0) | 0x10 | 0x00 | 0xFF = uspeh |
| Relay ON (pos 1) | 0x2F | 0x00 | 0xFF = uspeh |
| Relay OFF (pos 1) | 0x20 | 0x00 | 0xFF = uspeh |
| SetLockPolarity (normal) | 0x0F | 0x00 | - |
| SetLockPolarity (inverted) | 0x7F | 0x00 | - |
| AudioSignal ON | 0xAF | 0x00 | - |
| AudioSignal OFF | 0xA0 | 0x00 | - |

### Status vrednosti

**LockState:** 0x00 = Zatvorena, 0xFF = Otvorena, ostalo = Greška
**IRSensorState:** 0x00 = Zauzeto, 0xFF = Prazno, ostalo = Greška

### Struktura odgovora

Pojedinačni upit:
```
[0x06, <Adresa>, <Komanda>, 0x00, <Status>, 0x00, 0x00, 0x03]
```

Bulk upit (GetAllLocksStatus / GetAllIRSensorStatus):
```
[0x06, <Adresa>, <Komanda>, <BrojBoxova>, <Status1>, <Status2>, ..., 0x03]
Dužina = 8 + BrojBoxova
```

### Komunikacioni tok

1. Očisti input/output bufere
2. Pošalji 8-bajtnu komandu
3. Čekaj odgovor (ReadTimeout)
4. Validiraj (ACK + ETX + echo check)
5. Ako ne uspe, ponovi do AttemptCount puta

### Konfiguracija (IControllerSettings)

ComPort, BaudRate, ReadTimeoutMils, WriteTimeoutMils, AttemptCount

### Mapiranje adresa (ControllerTypeId=1, 8 brava)

| Pozicija | Adresa (dec) | Adresa (hex) |
|---|---|---|
| 0 | 1 | 0x01 |
| 1 | 2 | 0x02 |
| 2 | 4 | 0x04 |
| 3 | 8 | 0x08 |
| 4 | 16 | 0x10 |
| 5 | 32 | 0x20 |
| 6 | 64 | 0x40 |
| 7 | 128 | 0x80 |

Za ostale tipove kontrolera: adresa = pozicija + 1
