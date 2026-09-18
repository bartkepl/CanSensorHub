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
