# 0003. Dostęp do przyrządów przez bibliotekę VISA wywoływaną bezpośrednio

**Stan:** przyjęta · **Data:** 2026-09-26
**Dotyczy:** `CanSensorHub.Metrology`

## Kontekst

Narzędzia kalibracyjne sterują przyrządami laboratoryjnymi: multimetrem
HP 34401A i jednostką akwizycji Agilent 34970A z kartą 34907A. Oba przyrządy
mają interfejs GPIB, dołączony do komputera przez adapter USB–GPIB widoczny
w systemie jako urządzenie USBTMC. Kolejne narzędzia mogą wymagać przyrządów
z interfejsem LAN albo szeregowym.

Na stanowisku zainstalowana jest implementacja VISA (NI-VISA), która obsługuje
wszystkie te interfejsy jednolicie przez adres zasobu (`GPIB0::22::INSTR`,
`USB0::...::INSTR`, `TCPIP0::...::INSTR`).

Wymagania:

1. Aplikacja **musi się budować i przechodzić testy** na maszynie bez VISA —
   w szczególności w CI.
2. Brak VISA na stanowisku użytkownika **nie może blokować** startu aplikacji
   ani narzędzia; narzędzie ma wtedy pracować na przyrządach symulowanych.
3. Wywołania przyrządów trwają do kilkudziesięciu sekund (seria odczytów przy
   długim czasie całkowania) i **nie mogą blokować wątku interfejsu**.

## Rozważane warianty

**A. Pakiet `Ivi.Visa` / `NationalInstruments.Visa`.** Oficjalne API .NET,
lecz zestawy są dystrybuowane przez instalator VISA do GAC, a pakiety NuGet
zawierają jedynie zestawy referencyjne. Build w CI wymagałby instalacji VISA
albo obejścia rozwiązywania zależności; narusza wymaganie 1.

**B. Most do pyVISA.** Dodatkowe środowisko uruchomieniowe i proces
pośredniczący, bez korzyści funkcjonalnej względem wariantu C.

**C. Bezpośrednie wywołania `visa64.dll` (P/Invoke).** Kilka funkcji
(`viOpenDefaultRM`, `viFindRsrc`, `viOpen`, `viWrite`, `viRead`,
`viSetAttribute`, `viClear`, `viClose`) wystarcza do pełnej obsługi SCPI.
Biblioteka jest ładowana przy pierwszym wywołaniu, więc jej brak ujawnia się
jako `DllNotFoundException` w chwili użycia, nie przy kompilacji.

**D. Bezpośrednia obsługa USBTMC.** Wiąże rozwiązanie z jednym interfejsem
i wymaga sterownika WinUSB zamiast sterownika dostarczanego z VISA.

## Decyzja

Wariant **C**.

- `VisaResourceManager.TryCreate` zwraca `null` i opis przyczyny, gdy VISA jest
  niedostępna; narzędzie przechodzi wtedy na przyrządy symulowane.
- Uchwyty sesji są typu `uint` (`ViUInt32`), a wartość atrybutu typu `nuint`
  (`ViAttrState` ma szerokość wskaźnika) — zgodnie z nagłówkiem `visa.h`
  dla procesów 64-bitowych.
- Każda operacja wejścia-wyjścia jest wykonywana w puli wątków
  i serializowana semaforem sesji. Po przekroczeniu czasu odczytu sesja jest
  czyszczona (`viClear`), aby spóźniona odpowiedź nie została odczytana jako
  odpowiedź na następne zapytanie.
- Sterowniki przyrządów zależą od interfejsu `IScpiSession`, nie od VISA.
  Testy jednostkowe podstawiają sesję rejestrującą komendy.
- Wyszukiwanie przyrządów domyślnie pomija porty szeregowe (`ASRL`). Zapytanie
  identyfikacyjne `*IDN?` wysłane na port należący do innego urządzenia —
  w tym adaptera CAN tej aplikacji — zakłóca jego protokół.

## Konsekwencje

- Build i testy nie wymagają VISA; pracę na przyrządach weryfikuje się na
  stanowisku.
- Obsługa zdarzeń VISA (SRQ, przerwania) nie jest dostępna. Procedury
  kalibracyjne tego nie wymagają: pomiary mają charakter zapytanie–odpowiedź.
- Implementacja wywołań jest zbieżna z projektem InstrumentControl tego samego
  autora. Kod jest powielony celowo, aby CanSensorHub nie zależał od innego
  repozytorium.
