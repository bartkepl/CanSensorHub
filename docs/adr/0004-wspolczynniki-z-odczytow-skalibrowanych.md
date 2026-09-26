# 0004. Współczynniki AFE wyznaczane z odczytów już skalibrowanych

**Stan:** przyjęta · **Data:** 2026-09-26
**Dotyczy:** `CanSensorHub.Tools.MpccAfeCal`

## Kontekst

Firmware MPCC przelicza napięcie na pinie ADC na napięcie wejściowe kanału
według modelu

```
V = k · (c1 · V_pin + c0),    k = 1 + tc · (T − Tref)
```

gdzie `c0`, `c1`, `tc` to parametry kalibracyjne kanału (`CAL_ADCn_C0/C1/TC`),
`Tref` — parametr `CAL_ADC_TREF`, a `T` — temperatura płytki z czujnika STS31.

Protokół węzła udostępnia wyłącznie wynik tego przeliczenia: `READ_ADC`
zwraca `V` w mV, a telemetria i `READ_SENSOR` — tę samą wartość. Napięcie na
pinie (`V_pin`) istnieje w firmware, ale nie jest wystawione na magistralę.

Wyznaczenie `c0` i `c1` wprost wymaga pary (`V_pin`, `V_ref`) w każdym
punkcie. Dostępna jest para (`V`, `V_ref`), przy czym `V` zależy od `V_pin`
liniowo przez bieżące współczynniki.

## Rozważane warianty

**A. Wystawienie `V_pin` przez firmware** (nowa komenda albo argument
`READ_ADC`). Rozwiązanie bezpośrednie, lecz wymaga zmiany protokołu i firmware
węzła, a narzędzie przestaje działać z węzłami bez tej zmiany.

**B. Przywrócenie współczynników domyślnych (`c0 = 0`, `c1` nominalne,
`tc = 0`) przed pomiarem**, a następnie odtworzenie `V_pin = V / c1`.
Wymaga dwukrotnego zapisu parametrów kalibracyjnych i pozostawia węzeł
w stanie nieskalibrowanym, jeśli procedura zostanie przerwana.

**C. Złożenie korekcji.** Dopasowanie prostej `V_ref = a · V + b` do
odczytów wykonanych przy bieżących współczynnikach i wyliczenie nowych
współczynników z warunku `k · (c1' · V_pin + c0') = a · V + b` dla każdego
`V_pin`:

```
c1' = a · c1
c0' = a · c0 + b / k
tc' = tc
```

## Decyzja

Wariant **C**.

- Dopasowanie odbywa się w dziedzinie odczytów węzła: `x` — średnia serii
  `READ_ADC`, `y` — średnia serii wzorca. Dla dwóch punktów prosta jest
  wyznaczona dokładnie, dla większej liczby — metodą najmniejszych kwadratów.
- `k` jest liczony ze średniej temperatury płytki w punktach kalibracji.
  Kanał z `tc ≠ 0` bez dostępnego odczytu temperatury nie jest kalibrowany.
- Współczynnik `tc` nie jest zmieniany. Jego wyznaczenie wymaga pomiarów
  w kilku temperaturach, czyli innego stanowiska i innego narzędzia
  (docs/adr/0002).
- Nowe współczynniki są zaokrąglane do `float` przed zapisem i przed
  porównaniem w odczycie zwrotnym — węzeł przechowuje je jako f32.
- Punkty, w których napięcie na pinie (odtworzone z bieżących współczynników)
  przekracza 99 % pełnej skali ADC, są wyłączane z dopasowania i z oceny
  sprawdzenia. Na kanałach 0–3 napięcie wejściowe 5 V odpowiada pełnej skali
  (VDDA ≈ 3,0 V), stąd domyślna górna granica zakresu 4,8 V.
- Wynik jest odrzucany, jeśli korekcja wzmocnienia przekracza 5 % albo
  korekcja przesunięcia 100 mV (wartości konfigurowalne). Korekcje tego rzędu
  wskazują na błąd połączeń, nie na tolerancję dzielnika.

## Konsekwencje

- Firmware i protokół pozostają bez zmian; narzędzie działa z każdą wersją
  firmware MPCC udostępniającą `READ_ADC` i parametry `CAL_ADCn_*`.
- Kalibracja jest poprawna niezależnie od stanu wyjściowego współczynników,
  w tym po wcześniejszej kalibracji. Kolejne przebiegi zbiegają do tego samego
  wyniku, a dopasowanie na węźle już skalibrowanym daje `a ≈ 1`, `b ≈ 0`.
- Rozdzielczość `READ_ADC` wynosi 1 mV. Rozdzielczość poniżej 1 mV uzyskuje
  się z uśredniania wielu odczytów, w których szum toru ADC działa jak dither.
  Uśrednianie wymaga odstępu rund odczytu nie krótszego niż okres pomiaru
  węzła (`MEASURE_PERIOD`); krótszy odstęp powtarza tę samą próbkę i zaniża
  rozrzut.
