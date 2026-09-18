# Bootloader CAN — plan wzmocnienia niezawodności

> **Aktualizacja 2026-09-16.** Od czasu spisania tego dokumentu narzędzie
> wgrało przez CAN dwa obrazy stacji pogodowej (3.0 build 8 oraz 3.1 build 9),
> oba zakończone weryfikacją i startem aplikacji. Objaw opisany poniżej nie
> wystąpił ponownie. Nie przesądza to, że przyczyna została usunięta: poprawki
> 1 i 2 mogły ją usunąć, mogła też pozostać utajona. Propozycje z dalszej części
> dokumentu pozostają aktualne jako wzmocnienie, a nie jako naprawa awarii
> bieżącej.

Status: **propozycja, nie zaimplementowana.** Spisane po nieudanej próbie flashowania
MPSWP przez CAN na prawdziwym sprzęcie (`BŁĄD: Brak odpowiedzi PROG_END.` przy pierwszym
bloku), gdy referencyjna aplikacja Pythonowa (`MPSWP/app`) na tym samym sprzęcie działa
poprawnie — więc przyczyna leży po stronie klienta C#, nie firmware'u ani magistrali.

## Co już sprawdzono i odrzucono (albo naprawiono, ale to nie był cały problem)

1. **Brak retry przy timeout PROG_END/PROG_START** (naprawione w `BootloaderFlasher.
   ProgramBlockAsync`) — timeout wcześniej wylatywał poza pętlę retry zamiast być w niej
   obsłużony jak status SEQ. Realny bug, ale samodzielnie nie rozwiązał problemu na
   sprzęcie użytkownika (błąd identyczny po 3 próbach).
2. **Dopasowywanie odpowiedzi przez UI-thread dispatcher** (naprawione — nowy
   `CanBusService.RawFrameReceived`, `BlCanLink` już go używa zamiast `FrameReceived`).
   Realna architektoniczna wada (seria 64 wysyłek PROG_DATA zapycha kolejkę Dispatcher.Post
   wpisami do Monitora magistrali, przez co dopasowanie odpowiedzi PROG_END mogło czekać
   w kolejce dłużej niż timeout) — ale też samodzielnie nie usunęła błędu na sprzęcie
   użytkownika. Zostaje w kodzie, bo to i tak realne ulepszenie, ale nie jest to (jedyna)
   przyczyna.

Skoro dwie sensowne architektonicznie poprawki nie usunęły objawu, a Python na tym samym
sprzęcie działa — kolejny krok **musi** być empiryczny, nie kolejną spekulacją.

## Krok 0 (najpierw to, zanim cokolwiek więcej): podejrzyj ruch na magistrali

Aplikacja ma już wbudowany surowy log CAN (zakładka **Monitor magistrali CAN**, przypięta
zawsze jako pierwsza zakładka) i działa na tym samym `CanBusService`, co okno Bootloadera —
więc rejestruje cały ruch TX/RX również podczas flashowania. Po kolejnej nieudanej próbie,
**przed wyczyszczeniem**, sprawdzić w tym logu:

- Czy `PROG_END` (OBJ w ID = `0x74`, pole FUNC=`0x08`/REQUEST) w ogóle poszedł jako TX?
- Czy cokolwiek wróciło jako RX z tym samym OBJ (`0x74`) i FUNC=`0x09`/RESPONSE — może
  wróciło, ale z jakiegoś powodu klient go nie skonsumował (inny błąd, nie brak ramki)?
- Ile realnie ramek TX poszło między `PROG_START` a `PROG_END` — dokładnie 64, czy mniej
  (co wskazywałoby na problem po stronie wysyłki, nie odbioru)?

To rozstrzygnie, czy problem jest po stronie *wysyłania* (ramki nie docierają na magistralę),
*odbioru* (odpowiedź przychodzi, ale gubi się w kliencie) czy *firmware'u* (naprawdę nie
odpowiada). Dalsze punkty poniżej zakładają, że to się jeszcze nie stało — każdy z nich
warto zweryfikować dopiero w świetle tego, co pokaże log, żeby nie strzelać po ciemku.

## Kandydaci do dalszego zbadania

### A. Echo nadawanych ramek po slcan
Wiele implementacji firmware slcan odsyła echo wysłanej komendy `T........` z powrotem po
tym samym porcie szeregowym (potwierdzenie zapisu, nie odbioru z magistrali). Jeśli WeAct
tak robi, każda z 64 ramek PROG_DATA wygenerowałaby dodatkowo "przychodzącą" ramkę w
`SlcanTransport.ReaderLoop` — filtr `FUNC != RESPONSE` w `BlCanLink.OnFrameReceived`
powinien to poprawnie odsiewać, ale warto sprawdzić w Monitorze magistrali, czy faktycznie
tak jest (nadmiar wpisów RX o FUNC=REQUEST tuż po serii PROG_DATA) — a jeśli tak, czy
`SlcanTransport.ReaderLoop` (odczyt bajt-po-bajcie, pojedynczy wątek) nadąża z takim
przepływem bez gubienia/mieszania ramek z prawdziwymi odpowiedziami.

### B. Throttling wysyłki PROG_DATA
Firmware (`bl_can.c`) jawnie zaznacza w komentarzu, że odbiór jest tylko-pollingowy z
3-głębokim FIFO. Klient obecnie wysyła 64 ramki PROG_DATA jedna po drugiej bez żadnego
opóźnienia. Nawet jeśli punkt 0 pokaże, że to nie jest (jedyna) przyczyna obecnego błędu,
dodanie throttlingu (np. krótka pauza co 3 ramki, dopasowana do głębokości FIFO) to tania
prewencja na przyszłość — nie polegać wyłącznie na mechanizmie SEQ-retry.

### C. Backoff między próbami bloku
Nieudana próba bloku dziś natychmiast powtarza cały PROG_START. Krótkie opóźnienie
(50–100 ms) przed ponowieniem dałoby firmware'owi czas na pełne wybudzenie się z
poprzedniej operacji (np. zapis flash w handlerze PROG_END), zamiast odpalać dokładnie ten
sam wzorzec obciążenia od razu ponownie.

### D. Timeout PROG_END skalowany do rozmiaru bloku
`bl_flash_program()` wykonuje się synchronicznie w handlerze PROG_END przed wysłaniem
odpowiedzi — stały timeout 2 s może nie dawać marginesu przy większych blokach/wolniejszej
geometrii flasha. Rozważyć timeout zależny od rozmiaru bloku zamiast stałej.

### E. Ujednolicenie dopatrywania odpowiedzi w pozostałych modułach
`MpccDeviceClient`/`MpswpDeviceClient` nadal subskrybują `FrameReceived` (kolejkowane przez
UI thread), tak jak wcześniej robił `BlCanLink`. Nie demonstrowały dotąd tego samego objawu
(nie poprzedza ich seria 64 wysyłek pod rząd), ale to ta sama potencjalna wada architektury
— warto ujednolicić na `RawFrameReceived` przy okazji, dla spójności.

### F. Bogatszy log per-próba
Dziś log Bootloadera pokazuje tylko przejścia etapów + błąd końcowy. Dodanie linii per
próba bloku ("blok @0x000: próba 2/3 — retry po SEQ/timeout") ułatwiłoby diagnozę kolejnych
przypadków bez konieczności odtwarzania tego z kodu źródłowego, tak jak teraz.

### G. Opcjonalny podgląd surowych ramek BL w oknie Bootloadera
Checkbox "loguj każdą ramkę protokołu BL" w oknie Bootloadera, dublujący do loga okna to,
co i tak widać w Monitorze magistrali — wygodniejsze przy diagnozie w terenie bez
przełączania zakładek.

## Kolejność wdrażania (rekomendacja na przyszłość)

1. Krok 0 — zebrać log Monitora magistrali z nieudanej próby (rozstrzyga resztę).
2. W zależności od wyniku: A (jeśli echo miesza się z odpowiedziami) albo bezpośrednio
   B+C (jeśli to jednak przeciążenie/timing).
3. D, E, F, G — niezależne usprawnienia, do zrobienia przy okazji, nie blokują diagnozy.
