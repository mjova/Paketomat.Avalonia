# Preneto iz Paketomat.v2

Jezgro se kopira iz `Paketomat.v2` i dalje razvija ovde. Dok Windows flota ne pređe na v4, ispravke iz v2 se prenose
ručno. Za svaki preneti deo upiši izvorni commit; pri svakom v2 izdanju proveri `git log <commit>..<novo> -- <putanja>`
i prenesi šta treba.

| Deo u v4 | Izvor u v2 | v2 commit | Datum | Napomena |
|---|---|---|---|---|
| `doc/plan.md` | `doc/plan-linux.md` (nikad commit-ovan u v2) | `0029647` (osnova pregleda) | 9.11.2026 | Reference `fajl:linija` u planu važe za v2 na tom commit-u |
| `src/Flatsoft.Common` | `Flatsoft.Common` | `0029647` | 9.11.2026 | `SerialClient`: van Windows-a `WriteAsync` je zvao sam sebe (beskonačna rekurzija), pa upisivao dvaput — sada samo `BaseStream.WriteAsync`; uklonjen `Debugger.Break`. Izbačeni nekorišćeni Serilog i `System.IO.Pipelines` paket (deo .NET 10) |
| `src/TopPos.Agent.Contracts` | `TopPos.Agent.Contracts` | `0029647` | 9.11.2026 | Bez izmena koda |
| `src/TopPos.Service` | `TopPos.Service` | `0029647` | 9.11.2026 | Bez izmena koda. Postman fajlovi nisu preneti (environment ima adresu i kredencijale dev servera) |
| `src/TopPos.PenPosService` | `TopPos.PenPosService` | `0029647` | 9.11.2026 | Bez izmena koda; izbačen `System.Text.Json` paket (deo .NET 10) |
| `src/TopPos.LockController` | `TopPos.LockController` | `0029647` | 9.11.2026 | Bez izmena koda |
| `src/TopPos.LockController.NCU16L` | `TopPos.LockController.NCU16L` | `0029647` | 9.11.2026 | Bez izmena koda; direktna referenca na `Flatsoft.Common`, izbačeni nekorišćeni Serilog paketi |
| `src/TopPos.LockController.NCU24L` | `TopPos.LockController.NCU24L` | `0029647` | 9.11.2026 | Bez izmena koda; direktna referenca na `Flatsoft.Common` |
| `src/TopPos.LockController.Serial` | `TopPos.LockController.Serial` | `0029647` | 9.11.2026 | Bez izmena koda |
| `src/TopPos.ECR` | `TopPos.ECR` | `0029647` | 9.11.2026 | `EcrLog`: folder iz `Configuration.logDirectory` (podrazumevano `Log` uz program), `Path.Combine`, greška upisa loga ne prekida transakciju, uklonjen nekorišćeni `WriteDebug`. `Configuration`: `AppContext.BaseDirectory` umesto `Assembly.Location`/`CodeBase`. Obrisan mrtav `hwid.cs` (WMI) i `EcrPortV1.calculateProdKey`, izbačen `System.Management`. VID/PID pretraga (Registry, samo Castles) samo na Windows-u. Uklonjena `app.ico` |

Svi projekti: `net10.0`, paketi 10.0.x, verzije u `Directory.Packages.props`, zajednička podešavanja u
`Directory.Build.props`.
