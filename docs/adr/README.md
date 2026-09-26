# Rejestr decyzji architektonicznych

Decyzje dotyczące wyłącznie aplikacji CanSensorHub, których uzasadnienie
przestałoby być oczywiste po kilku miesiącach. Decyzje wiążące więcej niż jedno
repozytorium rodziny CANbusSensors są zapisywane w repozytorium `CanBusCommon`.

Zapis obejmuje **kontekst, rozważane warianty i konsekwencje**, nie samą treść
decyzji. Decyzja bez zapisanego powodu bywa po czasie odwracana przez kogoś, kto
nie zna ograniczenia, które ją wymusiło.

| Nr | Decyzja | Stan |
|---|---|---|
| [0001](0001-narzedzia-jako-osobne-projekty.md) | Narzędzia serwisowe jako osobne projekty wykrywane w czasie działania, włączane właściwością `WithTools` | przyjęta |
| [0002](0002-jedno-narzedzie-jedna-wielkosc.md) | Jedno narzędzie obsługuje jeden typ urządzenia i jedną wielkość; przyrządy i obliczenia we wspólnej bibliotece `Metrology` | przyjęta |
| [0003](0003-dostep-do-przyrzadow-przez-visa.md) | Dostęp do przyrządów przez bezpośrednie wywołania `visa64.dll`; build i testy bez VISA | przyjęta |
