# 0002. Jedno narzędzie — jeden typ urządzenia i jedna wielkość

**Stan:** przyjęta · **Data:** 2026-09-26
**Dotyczy:** projekty `CanSensorHub.Tools.*`, `CanSensorHub.Metrology`

## Kontekst

Pierwszym narzędziem serwisowym jest kalibracja siedmiu wejść napięciowych
(AFE) węzła MPCC. Przewidywane są kolejne: kalibracja czujników temperatury
(MPCC — STS31, MPSWP — zestaw czujników środowiskowych) z użyciem komory
klimatycznej, a w dalszej perspektywie kalibracje nowych węzłów.

Poszczególne kalibracje różnią się niemal wszystkim, co widzi operator:
wielkością, modelem korekcji w firmware, parametrami węzła, procedurą,
kryteriami akceptacji i zestawem przyrządów. Wspólne są natomiast:
komunikacja z przyrządami, statystyka serii pomiarów, dopasowanie modelu
i zapis raportu.

## Rozważane warianty

**A. Jedno ogólne narzędzie kalibracyjne** z wyborem urządzenia i wielkości.
Wymaga warstwy abstrakcji nad modelami korekcji różnych węzłów, zanim
istnieje drugi przypadek, który by ją weryfikował. Interfejs musi obsłużyć
wszystkie warianty naraz, co utrudnia pracę w najczęstszym z nich.

**B. Jedno narzędzie na parę urządzenie–wielkość**, z częścią wspólną
wydzieloną do biblioteki.

## Decyzja

Wariant **B**.

- Projekt narzędzia nosi nazwę `CanSensorHub.Tools.<Urządzenie><Wielkość>Cal`,
  np. `CanSensorHub.Tools.MpccAfeCal`. Pole `TargetModuleId` wskazuje moduł
  urządzenia, którego narzędzie dotyczy.
- Biblioteka `CanSensorHub.Metrology` (bez WPF i bez zależności od warstwy
  CAN) zawiera: dostęp do przyrządów przez VISA, sterowniki przyrządów,
  przyrządy symulowane, statystykę serii, dopasowanie liniowe i zapis raportu
  CSV.
- Wiedza o urządzeniu — parametry kalibracyjne, model korekcji, sekwencja
  zapisu do węzła — należy wyłącznie do projektu narzędzia.

## Konsekwencje

- Nowe narzędzie nie zmienia narzędzi istniejących; może zostać usunięte
  z buildu niezależnie od pozostałych przez usunięcie jego referencji
  w powłoce.
- Sterownik przyrządu dodany dla jednego narzędzia (np. komora klimatyczna)
  jest od razu dostępny dla pozostałych.
- Uogólnienie procedur kalibracyjnych jest odłożone do chwili, w której
  istnieją co najmniej dwa narzędzia o wspólnym przebiegu; wtedy wspólna
  część przechodzi do `Metrology` na podstawie rzeczywistych przypadków.
