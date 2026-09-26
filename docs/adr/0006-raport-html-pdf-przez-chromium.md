# 0006. Raport HTML, PDF przez przeglądarkę Chromium bez okna

**Stan:** przyjęta · **Data:** 2026-09-26
**Dotyczy:** `CanSensorHub.Metrology`, projekty `CanSensorHub.Tools.*`

## Kontekst

Raport CSV sesji kalibracji zawiera dane surowe i nadaje się do ponownego
przeliczenia, lecz nie do odczytu przez człowieka ani do archiwizacji jako
dokument (świadectwo kalibracji węzła). Potrzebny jest raport czytelny —
z wynikiem, tabelami i wykresem błędu — w postaci do wydruku i w PDF.

Ograniczenia:

1. Wszystkie zależności projektu są na licencji MIT
   (`THIRD-PARTY-NOTICES.md`); nowa zależność musi tę własność zachować.
2. Raport ma wyglądać tak samo na ekranie, na wydruku i w PDF — utrzymywanie
   dwóch niezależnych układów tej samej treści prowadzi do rozbieżności.
3. Build i testy nie mogą wymagać przeglądarki ani innego oprogramowania
   zainstalowanego na maszynie CI.

## Rozważane warianty

**A. QuestPDF.** Wygodne API układu, lecz licencja Community nie jest MIT
i wiąże się z warunkami zależnymi od przychodu użytkownika; narusza
ograniczenie 1.

**B. PdfSharp/MigraDoc (MIT).** PDF bez zewnętrznego oprogramowania, lecz
układ raportu musiałby powstać drugi raz, w modelu dokumentu MigraDoc,
obok wersji HTML; narusza ograniczenie 2.

**C. Samodzielny HTML i konwersja przeglądarką Chromium bez okna**
(`--headless --print-to-pdf`). Jeden układ strony (HTML z wbudowanym stylem
i wykresem SVG) obsługuje ekran, wydruk i PDF. Przeglądarka jest programem
zewnętrznym, wywoływanym jako proces — nie jest zależnością kompilacji.

## Decyzja

Wariant **C**.

- Raport HTML powstaje przy każdej operacji, obok raportu CSV, pod tą samą
  nazwą. Jest samodzielny: styl wbudowany, wykres jako SVG, bez skryptów
  i plików zewnętrznych. Treść jest escapowana; bez escapowania wstawiany
  jest wyłącznie SVG generowany przez kod.
- PDF powstaje na żądanie operatora z pliku HTML. Wyszukiwana jest kolejno
  przeglądarka Google Chrome, a następnie Microsoft Edge (obecny w każdym
  Windows 10/11); obie obsługują te same przełączniki.
- Przeglądarka jest uruchamiana z osobnym, tymczasowym katalogiem profilu.
  Bez niego wywołanie przy otwartej przeglądarce użytkownika zostaje
  przekazane do działającej instancji i kończy się bez utworzenia pliku.
- Elementy ogólne — budowanie dokumentu HTML, wykres SVG, konwersja do PDF —
  należą do `CanSensorHub.Metrology.Reporting`; układ raportu konkretnej
  kalibracji — do projektu narzędzia (docs/adr/0002).

## Konsekwencje

- Zależności projektu pozostają bez zmian.
- Brak zarówno Chrome, jak i Edge uniemożliwia wyłącznie utworzenie PDF;
  raport HTML jest dostępny i można go wydrukować do PDF z dowolnej
  przeglądarki.
- Testy obejmują budowanie HTML, SVG i dobór przeglądarki; sama konwersja
  do PDF jest sprawdzana na stanowisku, nie w CI.
- Wygląd PDF zależy od silnika przeglądarki. Styl raportu używa wyłącznie
  podstawowych właściwości CSS drukowania (`@page`, `break-inside`), więc
  różnice między wersjami Chromium są pomijalne.
