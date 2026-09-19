# Budowanie i uruchamianie

## Instalacja gotowej wersji

Do używania aplikacji nie trzeba niczego budować. Instalator jest przy każdym wydaniu:
[Releases](https://github.com/bartkepl/CanSensorHub/releases/latest) →
`CanSensorHub-win-Setup.exe`.

Pakiet niesie własny runtime .NET, więc nie wymaga niczego wcześniej zainstalowanego.
Instaluje się dla bieżącego użytkownika, bez uprawnień administratora.

!!! tip "Aplikacja aktualizuje się sama"
    Zainstalowana wersja przy starcie sprawdza, czy w repozytorium jest nowsze wydanie,
    i po zgodzie użytkownika pobiera je oraz uruchamia się ponownie. Szczegóły —
    [Wydania i automatyczna aktualizacja](#wydania-i-automatyczna-aktualizacja).

Alternatywnie `CanSensorHub-win-Portable.zip`: rozpakuj i uruchom `CanSensorHub.exe`.
Wersja przenośna nie aktualizuje się sama.

## Wymagania

| Element | Wymóg |
|---|---|
| Środowisko | .NET 10 SDK, Windows |
| Sprzęt (opcjonalnie) | adapter WeActStudio USB2CANFDV1 we firmware **slcan**, podłączony jako port COM |

Bez adaptera aplikacja działa w pełni w **trybie symulatora**: fałszywe węzły
pracują w procesie aplikacji i generują realistyczną telemetrię.

## Skrypty

```powershell
./build.ps1                          # dotnet build (Debug)
./build.ps1 -Configuration Release
./build.ps1 -Clean                   # pełne czyszczenie bin/obj przed budową
./build.ps1 -Publish                 # katalog samodzielny (publish/Debug|Release)

./run.ps1                            # zbuduj i uruchom
./run.ps1 -NoBuild                   # uruchom bez ponownej budowy

./build_release.ps1                  # publikacja Release do CanSensorHub\build\
./build_release.ps1 -SelfContained   # z dołączonym środowiskiem uruchomieniowym
./build_release.ps1 -Pack            # jw. + instalator Velopack w artifacts\releases
```

Bezpośrednio przez `dotnet`:

```powershell
dotnet build CanSensorHub.slnx
dotnet run --project src/CanSensorHub.App/CanSensorHub.App.csproj
dotnet test
```

## Testy

Warstwa `CanSensorHub.Core` jest pokryta testami jednostkowymi w
`tests/CanSensorHub.Core.Tests` — układ 29-bitowego identyfikatora, kody FUNC i statusu,
kodowanie ładunków telemetrii i statusu, składanie transferów segmentowanych, obie sumy
kontrolne oraz parser obrazu firmware (`.bin` i Intel HEX).

```powershell
dotnet test
```

Projekt testowy celuje w `net10.0` bez WPF, więc testy nie wymagają ani sprzętu, ani
Windows Desktop SDK. Uruchamiają się w CI przy każdym pushu i pull requeście.

!!! note "Dlaczego akurat te obszary"
    Testy pilnują tego, co musi zgadzać się co do bitu z firmware węzła. Błąd w tej
    warstwie nie objawia się wyjątkiem, tylko wartością, która wygląda poprawnie
    i opisuje co innego — dokładnie jak w przypadku 121 próbek opisanym niżej.

## Wydania i automatyczna aktualizacja

Każdy push na `main` uruchamia workflow `release.yml`, który buduje, testuje, publikuje
paczkę self-contained dla `win-x64`, pakuje ją przez [Velopack](https://velopack.io/)
i tworzy wydanie w GitHubie.

### Numerowanie wersji

Plik `VERSION` w katalogu głównym trzyma `MAJOR.MINOR`. Numer poprawki to numer przebiegu
GitHub Actions, który rośnie sam przy każdym pushu — dzięki temu wersja nie musi wracać
commitem do repozytorium i nie powstaje pętla *push → build → push*.

Podniesienie wersji głównej albo podrzędnej to edycja pliku `VERSION`.

### Co trafia do wydania

| Plik | Do czego |
|---|---|
| `CanSensorHub-win-Setup.exe` | instalator dla nowych instalacji |
| `CanSensorHub-win-Portable.zip` | wersja przenośna, bez instalacji i bez aktualizacji |
| `CanSensorHub-<wersja>-full.nupkg` | paczka, którą pobiera mechanizm aktualizacji |
| `releases.win.json`, `RELEASES` | manifest, po którym klient rozpoznaje nową wersję |

Usunięcie manifestu albo paczki `.nupkg` z wydania zatrzymuje automatyczne aktualizacje
dla wszystkich zainstalowanych kopii — wydania nie należy edytować ręcznie.

### Jak to działa po stronie aplikacji

`Program.Main` uruchamia `VelopackApp.Build().Run()` jako pierwszą instrukcję procesu
(dlatego `App.xaml` jest w projekcie zwykłą stroną, a nie `ApplicationDefinition` —
WPF nie generuje wtedy własnej metody `Main`). Zaraz potem aplikacja odpytuje wydania
w repozytorium i przy nowszej wersji pyta użytkownika o zgodę.

Cały ten etap jest celowo odporny na błędy — to narzędzie do obsługi sprzętu, więc brak
sieci ani uszkodzone wydanie nie mogą uniemożliwić jego uruchomienia:

- sprawdzanie ma limit **10 sekund**, po którym aplikacja startuje normalnie,
- uruchomienie spoza instalacji (`dotnet run`, katalog `build\`) pomija sprawdzanie,
- wersja, której instalacja nie powiedzie się **trzy razy**, przestaje być proponowana,
- przebieg zapisuje się w `%AppData%\CanSensorHub\update.log`.

Ustawienia leżą w `%AppData%\CanSensorHub`, poza katalogiem aplikacji, więc aktualizacja
ich nie rusza.

### Zbudowanie instalatora lokalnie

```powershell
./build_release.ps1 -Pack
```

Wynik ląduje w `artifacts\releases`. Paczka zbudowana lokalnie nadaje się do sprawdzenia
samego instalatora; aktualizacji z niej nie da się przetestować, bo klient szuka wydań
w repozytorium GitHub, a nie w katalogu lokalnym.

## Praca z aplikacją

1. **Pasek połączenia.** Wybór trybu: *Symulator* albo *Adapter WeAct (slcan)*
   z portem COM i przepływnością — domyślnie 500 kbit/s, zgodnie z węzłami.
   Aktywne jest jedno połączenie, współdzielone przez wszystkie urządzenia.
2. **Dodanie urządzenia.** Typ modułu z listy zamkniętej oraz adres węzła
   (zapis szesnastkowy `0x10` albo dziesiętny `16`), z podpowiedzią adresu
   domyślnego. W trybie symulatora uruchamiany jest odpowiadający mu fałszywy
   węzeł.
3. **Zakładki urządzeń.** Pulpit z kartami wartości i nagrywaniem CSV, wykresy
   na żywo, parametry, czujniki, status, czas RTC, log zdarzeń, bootloader.
4. **Monitor magistrali.** Surowy log ramek, z pauzą, filtrem i eksportem CSV.

Ustawienia zapisywane są w `%AppData%\CanSensorHub\settings.json`
i przywracane przy starcie, po ponownym nawiązaniu połączenia.

## Kolejność przy aktualizacji firmware

Aplikacja jest warunkiem wstępnym aktualizacji węzła: wgrywanie odbywa się
przez jej narzędzie bootloadera, a po starcie to ona dekoduje ramkę
identyfikacyjną nowego firmware.

!!! warning "Hosty aktualizuje się przed węzłami"
    Węzeł pracujący już w nowym profilu, obsługiwany przez hosta w profilu
    starszym, jest dekodowany według nieaktualnych tablic znaczeń. Podczas
    migracji stacji pogodowej powstało w ten sposób 121 próbek zapisanych pod
    niewłaściwą nazwą wielkości — dane wyglądały poprawnie, lecz opisywały co
    innego.

Zalecana kolejność:

1. Zbudować i uruchomić aplikację w wersji obsługującej docelowy profil.
2. Zarchiwizować obraz dotychczasowy wraz z manifestem.
3. Zweryfikować obraz dotychczasowy w węźle (`BL_OP_VERIFY`) — potwierdza
   bajtową tożsamość archiwum przed nadpisaniem.
4. Wgrać obraz nowy.
5. Sprawdzić ramkę identyfikacyjną, status i telemetrię po starcie.
