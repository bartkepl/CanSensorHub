# Warstwa protokołu

Implementacja po stronie hosta w języku C#. Specyfikacja jest wspólna dla całej
magistrali i opisana w repozytorium
[CanBusCommon](https://github.com/bartkepl/CanBusCommon); ta strona obejmuje
sposób jej realizacji w aplikacji.

## Otoczka identyfikatora

`CanId` pakuje i rozpakowuje 29-bitowy identyfikator na pola `FUNC`, `NODE`,
`OBJ` i `ARG`. Klasa jest niezależna od modułów — ten sam kod obsługuje każdy
węzeł magistrali.

## Tożsamość urządzenia

`DeviceIdentity` dekoduje ramkę identyfikacyjną. Metoda `Parse` rozpoznaje
profil **po długości bufora** i wypełnia tylko te pola, które w danym profilu
istnieją:

| Długość | Profil | Zakres pól |
|---|---|---|
| 4 B | 0 | wersja protokołu, wersja firmware, adres węzła |
| 21 B | 1 | dodatkowo wersja sprzętu, numer budowy, znaczniki, UID |
| 28 B | 2 | dodatkowo `DeviceType`, `ProfileVersion`, `Capabilities` |

Bufor krótszy niż 4 B jest odrzucany wyjątkiem. Pola nieobecne w danym profilu
pozostają puste, a nie wypełnione wartościami domyślnymi — host odróżnia zatem
„węzeł nie podał" od „węzeł podał zero".

`DeviceCapability` odwzorowuje bitmapę zdolności węzła jako typ flagowy,
o wartościach zgodnych z warstwą wspólną. Aplikacja dostosowuje na tej
podstawie interfejs do tego, co węzeł faktycznie obsługuje.

`CommandGuard` gromadzi bajty zabezpieczające komend nieodwracalnych
w jednym miejscu, wspólnym dla wszystkich modułów.

## Składanie transferów segmentowanych

`StreamAssembler` w `Protocol/Messages.cs` składa odpowiedzi wieloramkowe.

!!! danger "Segment końcowy nie kończy transferu"
    Pierwotna implementacja finalizowała transfer w chwili odebrania segmentu
    oznaczonego jako ostatni i zwracała sklejony wynik **bez sygnału błędu**.
    Dla ramki identyfikacyjnej dawało to bufor 21-bajtowy — wartość
    nieodróżnialną od poprawnej odpowiedzi profilu 1. Węzeł profilu 2 zostałby
    rozpoznany jako starszy, a aplikacja użyłaby wobec niego niewłaściwych
    tablic znaczeń.

    Usterka zamieniała błąd transmisji w dane wyglądające na poprawne. Wykryto
    ją dlatego, że numer UID kończył się sekwencją `FF 7F 08 00` — będącą
    w istocie bitmapą `CAPABILITIES` zapisaną w kolejności little-endian.

Implementacja obowiązująca: segment oznaczony jako ostatni wyznacza **liczbę**
segmentów, lecz transfer jest kompletny dopiero po zebraniu wszystkich indeksów
od zera. Kolejność odbioru nie musi odpowiadać kolejności nadania. Metoda
`Expire()` zamyka transfer przeterminowany, zgłaszając brakujące indeksy.

!!! danger "Niekompletny transfer nie może czekać na następne odpytanie"
    Transfer z brakującym segmentem pozostawał w buforze bez ograniczenia
    czasu. Następne odpytanie tego samego obiektu dopełniało go własnymi
    segmentami, a wynik łączył dane z dwóch odpytań. Po jednej zgubionej
    ramce składanie przesuwało się o transfer na stałe: w odpowiedzi
    READ_SENSOR wielkości o indeksach do zgubionego segmentu włącznie były
    świeże, pozostałe — sprzed jednego okresu odpytywania. Na wykresie część
    kanałów pokrywała się z telemetrią, a część była przesunięta o okres
    odpytywania, bez żadnego sygnału błędu.

Niekompletny transfer jest porzucany, gdy:

- od jego ostatniego segmentu minęło więcej niż `SegmentGapTimeout`
  (domyślnie 500 ms) — segmenty jednego transferu dzielą milisekundy,
  kolejne odpytania tego samego obiektu sekundy;
- nadchodzi segment o indeksie już zebranym, lecz o innej treści — należy
  on do kolejnego transferu. Ten sam indeks o identycznej treści jest
  powtórzeniem ramki przez kontroler CAN i niczego nie zmienia.

Porzucenie zgłasza zdarzenie `TransferAbandoned` z brakującymi indeksami.
Moduły urządzeń przekazują je do dziennika zdarzeń, więc utrata ramek
na magistrali jest widoczna wprost. Reguły działają wyłącznie po stronie
hosta i nie zmieniają formatu ramek.

Ta sama poprawka została wprowadzona w bibliotece `mpcan` po stronie hostów
pythonowych — defekt występował w obu implementacjach niezależnie.

## Parametry

`Params` opisuje typ, zakres i sposób przechowywania parametru. Moduł
urządzenia dostarcza jeden deskryptor na parametr, wraz z jawnie wybraną
kontrolką interfejsu: pole wyboru dla flag 0/1, lista rozwijana dla trybów
nazwanych, pole liczbowe dla pozostałych.

## Zgodność z profilami wcześniejszymi

Aplikacja obsługuje jednocześnie węzły w profilach 0, 1 i 2. Wynika to
z wymogu warstwy wspólnej: dopóki na magistrali może pojawić się węzeł
niemigrowany, host musi znać dawne znaczenia numerów.

Niezgodność **typu urządzenia** jest traktowana poważniej niż różnica wersji
firmware. Różnica wersji oznacza zwykle starszy lub nowszy egzemplarz tego
samego urządzenia; niezgodny typ oznacza, że pod danym adresem pracuje coś
innego, niż zakłada moduł — a wtedy interpretacja telemetrii byłaby błędna
w całości.
