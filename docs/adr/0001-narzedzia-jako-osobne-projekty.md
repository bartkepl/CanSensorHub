# 0001. Narzędzia serwisowe jako osobne projekty wykrywane w czasie działania

**Stan:** przyjęta · **Data:** 2026-09-26
**Dotyczy:** CanSensorHub.App, CanSensorHub.Core, projekty `CanSensorHub.Tools.*`

## Kontekst

Obok codziennej obsługi węzłów (moduły urządzeń: telemetria, parametry,
czujniki, bootloader) stanowisko wymaga czynności wykonywanych rzadko
i wymagających dodatkowego sprzętu — w pierwszej kolejności kalibracji
wejść napięciowych MPCC z użyciem zadajnika i multimetru wzorcowego.

Czynności te mają trzy cechy, które odróżniają je od modułów urządzeń:

1. **Dotyczą stanowiska, nie pojedynczego węzła w pracy.** Wymagają
   przyrządów laboratoryjnych i biblioteki VISA, których zwykły użytkownik
   aplikacji nie posiada.
2. **Wykonywane są okazjonalnie.** Stała obecność w interfejsie codziennej
   pracy — np. jako podzakładka modułu — zajmuje miejsce i zwiększa ryzyko
   przypadkowego uruchomienia operacji zmieniającej metrologię węzła.
3. **Mogą być zbędne w danym wydaniu.** Musi istnieć możliwość zbudowania
   aplikacji bez nich, bez ingerencji w kod powłoki.

## Rozważane warianty

**A. Podzakładka w module urządzenia.** Najmniej kodu, ale narusza punkt 2
i wiąże zależności przyrządowe (VISA) z modułem, który jest potrzebny każdemu.
Wyłączenie z buildu wymagałoby kompilacji warunkowej w module.

**B. Kompilacja warunkowa w powłoce (`#if TOOLS`).** Spełnia punkt 3, lecz
każde nowe narzędzie wymaga zmian w powłoce w miejscach otoczonych
dyrektywami, które nie są kompilowane w każdej konfiguracji — błędy ujawniają
się dopiero w buildzie z przeciwną wartością symbolu.

**C. Ładowanie zestawów z katalogu `plugins/` bez referencji projektowej.**
Pełna niezależność, ale zestaw narzędzia i jego zależności (ScottPlot,
Metrology) musiałyby być kopiowane do katalogu publikacji osobnym krokiem,
a zgodność wersji zależności nie byłaby pilnowana przez kompilator.

**D. Osobny projekt, warunkowa referencja projektowa, wykrywanie w czasie
działania.** Powłoka zawiera wyłącznie kontrakt `IHubTool` i mechanizm
wykrywania; zestawy narzędzi trafiają do katalogu wynikowego przez
`ProjectReference` warunkowaną właściwością MSBuild.

## Decyzja

Wariant **D**.

- Kontrakt `IHubTool`, kontekst `HubToolContext` i atrybut `HubToolAttribute`
  znajdują się w `CanSensorHub.Core` (przestrzeń `Tools`), bez zależności od WPF.
- Narzędzie to projekt `CanSensorHub.Tools.<Urządzenie><Wielkość>`, oznaczony
  atrybutem zestawu `[assembly: HubTool(typeof(...))]`.
- Powłoka przy starcie przegląda pliki `CanSensorHub.Tools.*.dll` w katalogu
  aplikacji. Przycisk „Narzędzia” jest widoczny tylko wtedy, gdy wykryto
  co najmniej jedno narzędzie.
- Właściwość `WithTools` (domyślnie `true`, również w wydaniach budowanych
  przez CI) warunkuje referencje projektowe powłoki do narzędzi. Skrypty
  `build.ps1` i `build_release.ps1` udostępniają przełącznik `-NoTools`.
- Narzędzie otwiera się jako niezależne okno i korzysta ze wspólnego
  połączenia CAN powłoki. Nie otwiera własnego połączenia.

## Konsekwencje

- Kod powłoki nie zawiera odwołań do typów narzędzi ani dyrektyw kompilacji
  warunkowej; build z `WithTools=false` kompiluje dokładnie ten sam kod powłoki.
- Zależności narzędzia (np. biblioteka przyrządów) są rozwiązywane przez
  kompilator jak każda inna referencja projektowa.
- Wadliwy zestaw narzędzia nie blokuje startu aplikacji: błąd wykrywania jest
  zgłaszany w pasku stanu, a pozostałe narzędzia są dostępne.
- Zestawy muszą zachować prefiks nazwy `CanSensorHub.Tools.`. Zestaw o innej
  nazwie nie zostanie wykryty, mimo że trafi do katalogu wynikowego.
