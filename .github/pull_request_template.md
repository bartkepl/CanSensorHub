## Co zmienia

<!-- Krótko: co i po co. Jeśli jest powiązane zgłoszenie — "Fixes #123". -->

## Jak sprawdzone

<!-- Symulator czy prawdziwy sprzęt? Jaki węzeł, jaka wersja firmware?
     Przy zmianach w bootloaderze: czy wgranie obrazu faktycznie przeszło. -->

- [ ] `dotnet build -c Release` — zero błędów, zero ostrzeżeń
- [ ] `dotnet test` — testy Core przechodzą
- [ ] Sprawdzone w trybie symulatora
- [ ] Sprawdzone na prawdziwej magistrali

## Wpływ na protokół

- [ ] Bez zmian w protokole
- [ ] Zmienia protokół — wymaga odpowiadającej zmiany w firmware (opisz niżej)

<!-- Zmiana w CanSensorHub.Core.Protocol bez zmiany po stronie firmware zepsuje
     działające instalacje. Wsparcie starszych wersji protokołu zostaje. -->
