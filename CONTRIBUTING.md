# Jak współtworzyć CanSensorHub

Dzięki za zainteresowanie. Projekt jest narzędziem do obsługi konkretnego sprzętu, więc
najcenniejsze są zgłoszenia z prawdziwej magistrali — logi, numery wersji firmware, opis
tego, co węzeł faktycznie odpowiedział.

## Zanim zaczniesz

- **Zgłoś problem przed większą zmianą.** Łatwiej uzgodnić kierunek w zgłoszeniu niż
  w gotowym pull requeście, zwłaszcza przy zmianach dotykających protokołu.
- **Protokół jest kontraktem z firmware.** Układ 29-bitowego identyfikatora, kody FUNC,
  bajt statusu i ładunki ramek muszą zgadzać się co do bitu z węzłami na magistrali.
  Zmiana w `CanSensorHub.Core.Protocol` bez odpowiadającej jej zmiany w firmware zepsuje
  działające instalacje. Wsparcie starszych wersji protokołu jest celowe i zostaje.

## Wymagania

- **.NET 10 SDK** (Windows) — aplikacja i moduły to WPF.
- Opcjonalnie adapter **WeActStudio USB2CANFDV1** we firmware slcan. Bez niego wszystko
  poza bootloaderem działa w trybie symulatora.

## Praca ze źródłami

```powershell
./build.ps1                 # dotnet build calego rozwiazania
./run.ps1                   # zbuduj i uruchom aplikacje
dotnet test                 # testy jednostkowe warstwy Core
```

Pełny opis wariantów budowania — w [`docs/budowanie.md`](docs/budowanie.md).

## Zgłaszanie pull requestów

1. **Jedna zmiana na pull request.** Refaktor i poprawka błędu w jednym PR sprawiają, że
   nie da się ocenić ani jednego, ani drugiego.
2. **Testy dla logiki.** Protokół, CRC i parser obrazu firmware są pokryte testami
   w `tests/CanSensorHub.Core.Tests`, przyrządy i obliczenia — w
   `tests/CanSensorHub.Metrology.Tests`, logika narzędzi — w `tests/CanSensorHub.Tools.*.Tests`.
   Nowa logika powinna trafić tam razem ze zmianą. Widoki WPF testów nie mają i nie jest
   to wymagane.
3. **Build bez ostrzeżeń.** `dotnet build -c Release` kończy się zerem błędów i zerem
   ostrzeżeń; nowy kod ma ten stan utrzymać.
4. **Opisz, jak to sprawdziłeś.** Symulator czy prawdziwy sprzęt? Jaki węzeł, jaka wersja
   firmware? Przy zmianach w bootloaderze — czy wgranie faktycznie przeszło.

### Styl

- Kod trzyma się konwencji .NET; komentarze wyjaśniają **dlaczego**, nie **co**.
- Komunikaty widoczne dla użytkownika są po polsku, nazwy w kodzie i komentarze
  techniczne — jak w otaczającym pliku.
- Opisy commitów: tryb rozkazujący, po polsku, z prefiksem obszaru, np.
  `protokol: ...`, `bootloader: ...`, `docs: ...`.

## Dodanie nowego modułu urządzenia

To najczęstszy rodzaj rozszerzenia — nie wymaga dotykania rdzenia. Krok po kroku opisuje
[`docs/moduly.md`](docs/moduly.md).

## Licencja

Zgłaszając wkład, godzisz się na udostępnienie go na licencji MIT — tej samej, na której
jest cały projekt (plik [`LICENSE`](LICENSE)).
