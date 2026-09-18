# Dodawanie modułu urządzenia

Moduł to osobny projekt WPF odwołujący się do `CanSensorHub.Core`. Cała otoczka
protokołu — identyfikator, klasy komunikatów, transfery segmentowane, status,
tożsamość urządzenia, bootloader — jest już w rdzeniu. Moduł dostarcza wyłącznie
to, czym jego urządzenie się różni.

## Zakres do dostarczenia

| Element | Rola |
|---|---|
| tablice protokołu | kanały telemetrii, czujniki, opcode'y i zdarzenia specyficzne, rejestr parametrów |
| `XxxDeviceClient` | typowana warstwa nad `CanBusService` dla jednego węzła |
| `XxxDeviceViewModel` | implementacja `IDeviceModuleInstance` |
| `XxxModuleDescriptor` | implementacja `IDeviceModuleDescriptor`, rejestrowana w `MainViewModel` |
| `XxxSimulator` | opcjonalnie: fałszywy węzeł do pracy bez sprzętu |

## `IDeviceModuleDescriptor`

Opis typu modułu w katalogu „Dodaj urządzenie".

| Składowa | Znaczenie |
|---|---|
| `ModuleId` | stabilny klucz maszynowy, np. `"MPCC"` |
| `DisplayName`, `ShortName`, `Summary` | teksty interfejsu |
| `DefaultNodeId` | adres domyślny, podpowiadany w oknie dodawania |
| `DeviceType` | wartość z rejestru typów urządzeń warstwy wspólnej |
| `BootloaderNodeId` | adres wkompilowany w bootloader, niezależny od `NODE_ID` aplikacji |
| `EnterBootloaderOpcode` | opcode wejścia w bootloader — `0x0D` w profilu 2 |
| `SupportsSimulation`, `StartSimulator` | obsługa trybu bez sprzętu |
| `CreateInstance` | utworzenie instancji urządzenia |

!!! note "`DeviceType` nie jest polem opisowym"
    Wartość służy do potwierdzenia, że pod danym adresem pracuje urządzenie
    tego rodzaju — zarówno przed zinterpretowaniem telemetrii, jak i przed
    wgraniem obrazu firmware. Wartość niezgodna z rejestrem typów urządzeń
    uczyni obie kontrole bezużytecznymi.

## Źródło tablic protokołu

Tablice modułu muszą odpowiadać plikowi `protocol/device.yaml` odnośnego
repozytorium urządzenia. Zgodność da się potwierdzić na żywym węźle:

| Komenda | Kontrola |
|---|---|
| `LIST_PARAMS` (`0x13`) | tablica parametrów w węźle odpowiada tablicy w module |
| `LIST_CHANNELS` (`0x14`) | zestaw nadawanych kanałów odpowiada oczekiwanemu |
| `GET_CAPABILITIES` (`0x12`) | węzeł obsługuje funkcje, których moduł używa |

Kontrole te są tańsze niż wykrycie rozbieżności na podstawie błędnych odczytów.

## Identyfikatory kanałów telemetrii

Kanał telemetrii jest adresowany **globalnym identyfikatorem wielkości
fizycznej** z rejestru warstwy wspólnej. Moduł nie nadaje własnych numerów:
`TEMP` to `0x01` na każdym węźle, `VOLTAGE_CH0` to `0x20` na każdym węźle.

Skutkiem ubocznym jest to, że host generyczny dekoduje telemetrię modułu bez
znajomości modułu. Własność ta jest wykorzystana przez rejestrator
`CanSensorLoggerPi`.

## Rejestracja

Deskryptor rejestruje się w `MainViewModel`, obok modułów istniejących.
`ModuleRegistry` udostępnia go w oknie dodawania urządzenia; lista jest zamknięta,
więc adres węzła podaje operator, a typ modułu wybiera z katalogu.
