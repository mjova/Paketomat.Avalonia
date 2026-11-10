@echo off
REM E2E testovi (Windows): pravi kiosk nad simulatorima servera i paketomata, UI preko UI Automation (doc\e2e.md).
REM Preduslovi: com0com parovi (skener COM18-COM19, POS COM20-COM21; za SerialControllerTests i kontroler vrata
REM COM22-COM23, bez njega se ti testovi preskacu), otkljucan desktop, zatvorena razvojna instanca kioska.
REM Tokom izvrsavanja ne dirati mis i tastaturu. Testovi sa pravim misem (RealMouse*) pomeraju mis.
REM Dodatni argumenti idu u dotnet test, npr.: run-e2e.cmd --filter "FullyQualifiedName~RecipientPickupTests"

setlocal
cd /d "%~dp0"

REM Prvo testovi jezgra kioska (bez UI-ja i hardvera): ako padnu, E2E se ne pokrece. Dodatni argumenti idu samo u E2E.
dotnet test tests\TopPos.Kiosk.Tests\TopPos.Kiosk.Tests.csproj -c Debug --disable-build-servers --logger "console;verbosity=minimal"
if errorlevel 1 (
	echo Testovi jezgra nisu prosli, E2E se ne pokrece.
	exit /b 1
)

REM Prozor kioska (ScreenResolution iz sablona) mora ceo stati na ekran (npr. pri skaliranju 125% tastatura ispadne van
REM ekrana). Pokrenuti testovi se ne mogu zaustaviti, pa se pita pre build-a.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tests\TopPos.Kiosk.E2E.Tests\check-screen.ps1" -Template "%~dp0tests\TopPos.Kiosk.E2E.Tests\Config\client.app-configuration.json"
if errorlevel 2 (
	echo Provera ekrana nije uspela, testovi se ne pokrecu.
	exit /b 1
)
if errorlevel 1 (
	choice /C DN /M "Prozor kioska ne staje na ekran. Ipak pokrenuti testove"
	REM Samo D (1) potvrdjuje; N (2) i prekid Ctrl+C/Ctrl+Break (0) zaustavljaju.
	if errorlevel 2 exit /b 1
	if not errorlevel 1 exit /b 1
)

REM Build celog resenja: E2E pokrece izlaze kioska i simulatora iz njihovih bin foldera. Bez build servera: posle
REM skripte ne ostaju MSBuild procesi.
dotnet build Paketomat.slnx -c Debug --disable-build-servers
if errorlevel 1 exit /b %errorlevel%

REM Izvestaj (HTML za browser, TRX za Visual Studio) ide u TestResults\E2E pored ove skripte.
for /f %%t in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd-HHmmss"') do set STAMP=%%t

set TOPPOS_E2E=1
dotnet test tests\TopPos.Kiosk.E2E.Tests\TopPos.Kiosk.E2E.Tests.csproj -c Debug --no-build --disable-build-servers --logger "console;verbosity=normal" --logger "html;LogFileName=e2e-%STAMP%.html" --logger "trx;LogFileName=e2e-%STAMP%.trx" --results-directory "%~dp0TestResults\E2E" %*
exit /b %errorlevel%
