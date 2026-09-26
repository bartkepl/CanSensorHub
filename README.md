# CanSensorHub

[![Build and Release](https://github.com/bartkepl/CanSensorHub/actions/workflows/release.yml/badge.svg)](https://github.com/bartkepl/CanSensorHub/actions/workflows/release.yml)
[![Dokumentacja](https://github.com/bartkepl/CanSensorHub/actions/workflows/docs.yml/badge.svg)](https://bartkepl.github.io/CanSensorHub/)
[![Licencja: MIT](https://img.shields.io/badge/licencja-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4.svg)](https://dotnet.microsoft.com/)

Modularna aplikacja PC (.NET 10 / WPF) do obsługi czujnikowych węzłów CAN z projektów
[MPCC](https://github.com/bartkepl/MPCC) (MIL_PSU_CAN) i
[MPSWP](https://github.com/bartkepl/MPSWP) (WeatherStationCan). Główna aplikacja
zarządza **jednym połączeniem** z adapterem **WeActStudio USB2CANFDV1** (firmware slcan)
i pokazuje surowy log ruchu na magistrali; każde urządzenie jest obsługiwane przez osobny,
wpinany **moduł** — na start MPCC i MPSWP, w przyszłości kolejne (np. GeigerProbe, obecnie
w budowie sprzętowej).

Oba obsługiwane węzły są **bit-kompatybilne** (identyczny układ 29-bitowego ID, kody FUNC/
status, ramka STREAM/STATUS) i mogą pracować jednocześnie na tej samej magistrali,
rozróżniane polem `NODE` — dlatego apka pozwala dodać dowolną liczbę instancji obu typów
naraz, każdą jako osobną zakładkę.

## Struktura rozwiązania

```
CanSensorHub.slnx
src/
  CanSensorHub.Core/              biblioteka klas (bez WPF) — wspólny fundament
    Can/                          CanFrame, transporty (slcan, symulator), CanBusService
    Protocol/                     wspólna ramka 29-bit, FUNC/status, STREAM, parametry
    Modules/                      kontrakt modułu (IDeviceModuleDescriptor) + rejestr
    Logging/                      LiveValueStore + bezpieczny szeroki logger CSV
    Bootloader/                   CRC32/16, parser .bin/.hex, flasher CAN

  CanSensorHub.Modules.Mpcc/      moduł MPCC (WPF class library)
    Protocol/                     tabele z protocol.yaml (kanały, czujniki, parametry...)
    MpccDeviceClient.cs           typowana warstwa żądań/odpowiedzi dla jednego węzła
    MpccSimulator.cs               fałszywy węzeł MPCC (tryb symulatora, bez sprzętu)
    ViewModels/ Views/             zakładki: Pulpit, Wykresy, Sterowanie, Parametry,
                                    Czujniki, Status, Czas, Zdarzenia, Bootloader

  CanSensorHub.Modules.Mpswp/     moduł MPSWP — lustrzana struktura, plus autokalibracja
                                    anteny AS3935; bez zakładki Sterowanie (brak wyjścia)

  CanSensorHub.Metrology/         biblioteka przyrządów (bez WPF): VISA, 34401A, 34970A/34907A,
                                    przyrządy symulowane, statystyka, dopasowanie, raport CSV

  CanSensorHub.Tools.MpccAfeCal/  narzędzie: kalibracja wejść napięciowych MPCC (menu Narzędzia);
                                    wyłączalne z buildu (-p:WithTools=false / -NoTools)

  CanSensorHub.App/               powłoka WPF (uruchamialna)
    MainWindow.xaml                pasek połączenia, zakładki urządzeń, Monitor magistrali
    Views/AddDeviceDialog.xaml     dodawanie urządzenia (typ modułu + adres NODE)
    Themes/                        jasny/ciemny motyw — podąża za ustawieniem Windows
    Services/                      trwałość ustawień (%AppData%\CanSensorHub)
```

## Instalacja

Gotowy instalator jest przy każdym wydaniu:
**[Releases](https://github.com/bartkepl/CanSensorHub/releases/latest) → `CanSensorHub-win-Setup.exe`**.

Instalator nie wymaga wcześniej zainstalowanego .NET — pakiet niesie własny runtime.
Aplikacja instaluje się dla bieżącego użytkownika (bez uprawnień administratora) i zakłada
skrót na pulpicie oraz w menu Start.

Dla instalacji bez instalatora w tym samym wydaniu jest `CanSensorHub-win-Portable.zip` —
rozpakuj i uruchom `CanSensorHub.exe`. Wersja przenośna **nie aktualizuje się sama**.

### Automatyczna aktualizacja

Zainstalowana aplikacja przy każdym starcie sprawdza, czy w tym repozytorium jest nowsze
wydanie. Jeśli jest, pyta o zgodę i po pobraniu uruchamia się ponownie w nowej wersji.
Całość obsługuje [Velopack](https://velopack.io/).

Sprawdzanie ma limit 10 sekund i jest odporne na błędy: brak sieci, prywatne repozytorium
czy uszkodzone wydanie nie blokują uruchomienia narzędzia. Wersja, której instalacja nie
powiedzie się trzy razy, przestaje być proponowana. Przebieg zapisuje się w
`%AppData%\CanSensorHub\update.log`.

Aktualizacja nie rusza ustawień — `settings.json` leży w `%AppData%\CanSensorHub`, poza
katalogiem aplikacji.

## Wymagania

Do samego używania aplikacji **nic nie trzeba instalować poza instalatorem**. Poniższe
dotyczy budowania ze źródeł:

- **.NET 10 SDK** (Windows).
- Do pracy z prawdziwym sprzętem: adapter **WeActStudio USB2CANFDV1** we firmware **slcan**
  podłączony jako port COM. Bez adaptera aplikacja działa w pełni w **trybie symulatora**
  (fałszywe węzły MPCC/MPSWP generujące realistyczną telemetrię w procesie, zero sprzętu).

## Budowanie i uruchamianie

```powershell
./build.ps1                # dotnet build (Debug)
./build.ps1 -Configuration Release
./build.ps1 -Clean         # pełne czyszczenie bin/obj przed budową
./build.ps1 -Publish       # samodzielny katalog gotowy do dystrybucji (publish/Debug|Release)

./run.ps1                  # zbuduj i uruchom CanSensorHub.App
./run.ps1 -NoBuild         # uruchom bez ponownego budowania

./build_release.ps1        # publikuj wersję Release do CanSensorHub\build\ (gotowy .exe, bez szukania w bin/obj)
./build_release.ps1 -SelfContained   # jw., z dołączonym runtime .NET (nie wymaga .NET na maszynie docelowej)
./build_release.ps1 -Pack            # jw. + instalator Velopack w artifacts\releases (to samo, co robi CI)
./build_release.ps1 -NoTools         # bez narzędzi serwisowych (menu „Narzędzia” znika)
```

Albo bezpośrednio przez `dotnet`:

```powershell
dotnet build CanSensorHub.slnx
dotnet run --project src/CanSensorHub.App/CanSensorHub.App.csproj
dotnet test
```

## Wydania

Każdy push na `main` buduje, testuje i publikuje wydanie — numer wersji to `MAJOR.MINOR`
z pliku `VERSION` plus numer przebiegu GitHub Actions jako numer poprawki. Zmiana wersji
głównej lub podrzędnej = edycja pliku `VERSION`; numer poprawki rośnie sam.

Do wydania trafiają instalator, wersja przenośna i paczka aktualizacyjna wraz z manifestem
`releases.win.json`, po którym zainstalowane aplikacje rozpoznają nową wersję.

## Jak to działa

1. **Pasek połączenia** (góra okna): wybór trybu — *Symulator* (bez sprzętu) albo *Adapter
   WeAct (slcan)* z wyborem portu COM i bitrate (500 kbit/s domyślnie, zgodnie z MPCC/MPSWP).
   Tylko jedno połączenie naraz — to ono jest współdzielone przez wszystkie dodane urządzenia.
2. **+ Dodaj urządzenie...** (aktywne po połączeniu): wybór typu modułu (lista zamknięta —
   ComboBox) i adresu węzła CAN `NODE_ID` (pole tekstowe, hex `0x10` lub dziesiętnie `16`,
   z podpowiedzią domyślnego adresu danego modułu). W trybie symulatora dodanie urządzenia
   automatycznie uruchamia jego fałszywy odpowiednik na wirtualnej magistrali.
3. **Zakładki urządzeń** — każde dodane urządzenie dostaje własną zakładkę z pełnym zestawem
   podzakładek (Pulpit z kartami wartości i nagrywaniem CSV, Wykresy na żywo, Parametry z
   automatycznie dobranym typem kontrolki — checkbox dla flag 0/1, combo dla trybów nazwanych,
   pole liczbowe dla reszty — Czujniki, Status/HART-like flagi, RTC, log Zdarzeń, Bootloader).
4. **Monitor magistrali** (dół okna) — surowy log wszystkich ramek TX/RX, module-agnostic
   (dekoduje tylko wspólną otoczkę FUNC/NODE/OBJ/ARG), z pauzą/filtrem/eksportem do CSV.
   To realizuje wymóg "aplikacja główna obsługuje logi komunikacji po magistrali CAN".

Ustawienia (tryb połączenia, port, bitrate, lista dodanych urządzeń) są zapisywane w
`%AppData%\CanSensorHub\settings.json` i przywracane przy starcie (po ponownym połączeniu).

## Dodawanie kolejnego modułu (np. GeigerProbe)

Moduł to osobny projekt WPF referencyjący `CanSensorHub.Core`, który dostarcza:

- tabele protokołu (kanały telemetrii, czujniki, opcode'y, zdarzenia, rejestr parametrów —
  jeden `ParamDescriptor` na parametr z jawnie wybraną kontrolką UI),
- `XxxDeviceClient` — typowaną warstwę nad `CanBusService`/`EnvelopeCodec` z Core,
- `XxxDeviceViewModel` implementujący `IDeviceModuleInstance` (widok + nagłówek zakładki),
- `XxxModuleDescriptor` implementujący `IDeviceModuleDescriptor` (rejestrowany w
  `MainViewModel` obok MPCC/MPSWP),
- opcjonalnie `XxxSimulator` do pracy bez sprzętu.

Cała otoczka 29-bit ID, FUNC/status, STREAM, ramka STATUS i flagi SYS/ERR są już gotowe we
wspólnym `CanSensorHub.Core.Protocol` (identyczne bit-w-bit u MPCC/MPSWP) — nowy moduł
dostarcza tylko to, co u niego rzeczywiście inne.

## Znane ograniczenia

- Programowanie firmware odbywa się wyłącznie przez **CAN** (przez adapter WeAct) — opcja
  UART została usunięta z aplikacji jako nieużywana. Znany, wciąż otwarty problem z
  niezawodnością tej ścieżki na prawdziwym sprzęcie (`Brak odpowiedzi PROG_END`) i plan
  dalszej diagnozy/wzmocnienia opisane w [`docs/BOOTLOADER_ROBUSTNESS.md`](docs/BOOTLOADER_ROBUSTNESS.md).
- Symulator nie implementuje bootloadera — zakładka Bootloader w trybie symulatora pozwoli
  wysłać `ENTER_BOOTLOADER`, ale dalszy flash wymaga prawdziwego (lub docelowo osobno
  zasymulowanego) urządzenia w trybie programowania.

## Dokumentacja

Pełna dokumentacja: **<https://bartkepl.github.io/CanSensorHub/>** — architektura, warstwa
protokołu, narzędzie bootloadera, dodawanie modułu urządzenia, kalibracja wejść MPCC,
rejestr decyzji architektonicznych (`docs/adr`).

Źródła strony leżą w katalogu `docs/` (MkDocs Material) i publikują się same przy każdej
zmianie na `main`:

```bash
pip install -r requirements-docs.txt
mkdocs serve     # podglad lokalny
mkdocs build     # statyczna strona w site/
```

## Testy

Testami jednostkowymi są pokryte: warstwa `CanSensorHub.Core` (protokół, sumy kontrolne,
składanie transferów segmentowanych, parser obrazu firmware, wykrywanie narzędzi),
biblioteka `CanSensorHub.Metrology` (komendy SCPI, statystyka, dopasowanie) oraz logika
narzędzia kalibracji na magistrali symulowanej:

```powershell
dotnet test
```

Testy nie wymagają sprzętu ani biblioteki VISA i przechodzą w CI przy każdym pushu i pull
requeście. Widoki WPF testów nie mają.

## Licencja

[MIT](LICENSE). Spis zależności wraz z ich licencjami — wszystkie są na MIT —
w [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).

Jak zgłaszać błędy i zmiany: [`CONTRIBUTING.md`](CONTRIBUTING.md).
