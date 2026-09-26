# Architektura

Rozwiązanie dzieli się na warstwy: rdzeń niezależny od interfejsu
graficznego, moduły urządzeń, narzędzia serwisowe z biblioteką przyrządów
oraz powłokę aplikacji.

```mermaid
flowchart TB
    subgraph APP["CanSensorHub.App — powłoka WPF"]
        MW[MainWindow: pasek połączenia, zakładki, monitor magistrali]
    end
    subgraph MOD["Moduły urządzeń"]
        M1[Modules.Mpswp]
        M2[Modules.Mpcc]
    end
    subgraph TOOLS["Narzędzia serwisowe — wyłączalne (WithTools)"]
        T1[Tools.MpccAfeCal]
        MET[Metrology: VISA, przyrządy, statystyka, raporty]
    end
    subgraph CORE["CanSensorHub.Core — bez WPF"]
        CAN[Can: transporty, CanBusService]
        PROT[Protocol: identyfikator, STREAM, tożsamość urządzenia]
        BL[Bootloader: CRC, obraz, flasher]
        LOG[Logging: LiveValueStore, logger CSV]
    end
    MW --> M1 & M2
    M1 & M2 --> CAN & PROT & BL
    MW --> CAN & LOG
    MW -. wykrywanie w czasie działania .-> T1
    T1 --> MET
    T1 --> M2 & CAN & PROT
```

## `CanSensorHub.Core`

Biblioteka klas bez zależności od WPF. Zawiera wszystko, co jest wspólne dla
każdego węzła magistrali.

| Przestrzeń | Zawartość |
|---|---|
| `Can` | `CanFrame`, transporty `SlcanTransport` i `SimulatedTransport`, `CanBusService` |
| `Protocol` | `CanId`, `Messages`, `Params`, `DeviceIdentity` |
| `Modules` | kontrakt modułu i rejestr typów modułów |
| `Tools` | kontrakt narzędzia serwisowego i wykrywanie narzędzi |
| `Logging` | `LiveValueStore`, `WideCsvLogger` |
| `Bootloader` | `Crc32`, `Crc16CcittFalse`, `FirmwareImage`, `BlCanLink`, `BootloaderFlasher` |

!!! note "Dlaczego rdzeń nie zna WPF"
    Interfejs `IDeviceModuleInstance` udostępnia widok modułu jako `object`,
    a nie `UIElement`. Dzięki temu projekt rdzenia nie musi odwoływać się do
    WPF, a rzutowanie wykonują projekty, które i tak od WPF zależą. Rozdział
    umożliwia testowanie warstwy protokołu bez uruchamiania interfejsu.

## Połączenie z magistralą

W danej chwili aktywne jest **jedno** połączenie, współdzielone przez wszystkie
dodane urządzenia. Tryby:

| Tryb | Zastosowanie |
|---|---|
| Adapter WeAct (slcan) | praca ze sprzętem, port COM, domyślnie 500 kbit/s |
| Symulator | praca bez sprzętu — fałszywe węzły w procesie aplikacji |

Dodanie urządzenia w trybie symulatora automatycznie uruchamia jego
odpowiednik na magistrali wirtualnej. Magistrala wirtualna ma też wspólne
wejście analogowe, na którym symulowane przyrządy narzędzi wymuszają napięcie
mierzone przez symulowane węzły. Symulowany węzeł korzysta z tego samego
kodeka co węzeł rzeczywisty, więc rozbieżność między nimi nie jest możliwa —
poza zakresem celowo niezasymulowanym.

## Moduł urządzenia

Moduł jest osobnym projektem odwołującym się do rdzenia. Dostarcza:

| Element | Rola |
|---|---|
| tablice protokołu | kanały, czujniki, opcode'y, zdarzenia, rejestr parametrów |
| `XxxDeviceClient` | typowana warstwa żądań i odpowiedzi dla jednego węzła |
| `XxxDeviceViewModel` | implementacja `IDeviceModuleInstance` — widok i nagłówek zakładki |
| `XxxModuleDescriptor` | implementacja `IDeviceModuleDescriptor` — wpis w katalogu modułów |
| `XxxSimulator` | opcjonalnie: fałszywy węzeł do pracy bez sprzętu |

Każdy moduł udostępnia komplet podzakładek: pulpit z kartami wartości
i nagrywaniem CSV, wykresy na żywo, parametry, czujniki, status, czas RTC, log
zdarzeń oraz bootloader. Moduł MPCC ma dodatkowo zakładkę sterowania wyjściami,
moduł MPSWP — autokalibrację anteny detektora wyładowań.

## Narzędzia serwisowe

Czynności okazjonalne, wymagające stanowiska pomiarowego — przede wszystkim
kalibracje — są realizowane przez narzędzia otwierane z menu „Narzędzia”
okna głównego, a nie przez zakładki modułów
([ADR 0001](adr/0001-narzedzia-jako-osobne-projekty.md)).

| Element | Rola |
|---|---|
| `IHubTool` | kontrakt narzędzia: nazwa, moduł docelowy, `Open(HubToolContext)` |
| `HubToolContext` | wspólne połączenie CAN, bieżąca lista dodanych urządzeń, okno właściciela |
| `[assembly: HubTool(...)]` | deklaracja narzędzia w zestawie `CanSensorHub.Tools.*` |
| `CanSensorHub.Metrology` | VISA, sterowniki przyrządów, przyrządy symulowane, statystyka, dopasowanie, raport CSV |

Powłoka nie odwołuje się do typów narzędzi. Zestawy narzędzi trafiają do
katalogu aplikacji przez referencje projektowe warunkowane właściwością
`WithTools`, a przy starcie są wykrywane po nazwie pliku i atrybucie zestawu.
Każde narzędzie obsługuje jeden typ urządzenia i jedną wielkość
([ADR 0002](adr/0002-jedno-narzedzie-jedna-wielkosc.md)).

| Narzędzie | Opis |
|---|---|
| `Tools.MpccAfeCal` | [kalibracja wejść napięciowych MPCC](kalibracja-mpcc-afe.md) |

## Monitor magistrali

Log wszystkich ramek nadanych i odebranych, **niezależny od modułów**: dekoduje
wyłącznie wspólną otoczkę `FUNC`/`NODE`/`OBJ`/`ARG`. Ruch węzła, dla którego nie
dodano modułu, jest zatem nadal widoczny i czytelny w zakresie warstwy wspólnej.

## Trwałość ustawień

Tryb połączenia, port, przepływność i lista dodanych urządzeń zapisywane są
w `%AppData%\CanSensorHub\settings.json` i przywracane przy starcie, po
ponownym nawiązaniu połączenia.
