# Budowanie i uruchamianie

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
```

Bezpośrednio przez `dotnet`:

```powershell
dotnet build CanSensorHub.slnx
dotnet run --project src/CanSensorHub.App/CanSensorHub.App.csproj
```

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
