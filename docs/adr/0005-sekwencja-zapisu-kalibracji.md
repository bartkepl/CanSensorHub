# 0005. Sekwencja zapisu współczynników z blokadą `CAL_LOCK`

**Stan:** przyjęta · **Data:** 2026-09-26
**Dotyczy:** `CanSensorHub.Tools.MpccAfeCal`

## Kontekst

Parametry kalibracyjne węzła MPCC są chronione parametrem uniwersalnym
`CAL_LOCK`: przy `CAL_LOCK = 1` zapis parametru oznaczonego jako kalibracyjny
kończy się statusem `ERR_READONLY`. Wartości zapisane przez `WRITE_PARAM`
obowiązują od razu w pamięci RAM, a do pamięci nieulotnej trafiają dopiero po
`SAVE_CONFIG`, który utrwala całą konfigurację jednocześnie — również bieżącą
wartość `CAL_LOCK`.

Zapis dotyczy czternastu parametrów (c0 i c1 siedmiu kanałów). Każdy krok może
się nie powieść: odmowa węzła, utrata ramki, przekroczenie czasu, anulowanie
przez operatora.

Wymagania:

1. Węzeł nie może zostać trwale pozostawiony z **częścią** nowych
   współczynników.
2. Węzeł nie może zostać trwale pozostawiony **bez blokady** kalibracji.
3. Zapis ma być potwierdzony odczytem zwrotnym, a nie wyłącznie statusem
   odpowiedzi.

## Rozważane warianty

**A. Zdjęcie blokady, zapis, `SAVE_CONFIG`, założenie blokady, drugi
`SAVE_CONFIG`.** Między dwoma zapisami do pamięci nieulotnej węzeł ma trwale
zdjętą blokadę; utrata zasilania w tym oknie narusza wymaganie 2.

**B. Zdjęcie blokady (RAM), zapis, odczyt zwrotny, założenie blokady (RAM),
jeden `SAVE_CONFIG`.** Pamięć nieulotna przechodzi w jednym kroku ze stanu
„stare współczynniki, blokada” do stanu „nowe współczynniki, blokada”.

## Decyzja

Wariant **B**, w kolejności:

1. Operator zatwierdza zestawienie współczynników przed i po zapisie.
2. `CAL_LOCK = 0`.
3. Dla każdego przyjętego kanału: `c1`, następnie `c0`. Każdy zapis musi
   zakończyć się statusem `OK`.
4. Odczyt zwrotny wszystkich zapisanych parametrów i porównanie na poziomie
   `float`.
5. `CAL_LOCK = 1`.
6. `SAVE_CONFIG`.
7. Odczyt `CAL_LOCK` — musi wynosić 1.
8. Sprawdzenie wielopunktowe na nowych współczynnikach.

Niepowodzenie w krokach 2–5 wywołuje wycofanie: zapis poprzednich wartości
`c1` i `c0` do kanałów już zmienionych oraz `CAL_LOCK = 1`, **bez**
`SAVE_CONFIG`. Wycofanie nie jest przerywane anulowaniem operacji.

## Konsekwencje

- Pamięć nieulotna węzła nigdy nie zawiera stanu pośredniego. Jeżeli samo
  wycofanie się nie powiedzie (np. węzeł przestał odpowiadać), reset węzła
  przywraca stan sprzed operacji z pamięci nieulotnej.
- Po operacji blokada jest zawsze założona, także gdy przed operacją była
  zdjęta. Pozostawienie zdjętej blokady wymaga osobnej, świadomej czynności
  operatora w zakładce parametrów modułu.
- `SAVE_CONFIG` utrwala całą konfigurację węzła. Zmiany innych parametrów
  wykonane w RAM przed kalibracją i niezapisane zostaną utrwalone razem
  z kalibracją. Narzędzie informuje o tym operatora przed zapisem.
- Każdy krok jest zapisywany w dzienniku sesji i w raporcie kalibracji.
