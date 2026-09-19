# Zależności i ich licencje

CanSensorHub jest udostępniany na licencji MIT (plik [`LICENSE`](LICENSE)). Poniżej spis
wszystkiego, co trafia do dystrybuowanej binarki albo jest używane w procesie budowania.

Wszystkie zależności są na licencji **MIT**, zgodnej z licencją tego projektu — dystrybucja
gotowego instalatora nie nakłada żadnych dodatkowych obowiązków poza zachowaniem not
licencyjnych, które realizuje ten plik.

## Pakiety NuGet (bezpośrednie)

| Pakiety | Wersja | Licencja | Projekt | Po co |
|---|---|---|---|---|
| `Velopack` | 1.2.0 | MIT | <https://github.com/velopack/velopack> | instalator i automatyczna aktualizacja aplikacji |
| `CommunityToolkit.Mvvm` | 8.4.2 | MIT | <https://github.com/CommunityToolkit/dotnet> | `ObservableObject`, `RelayCommand` — warstwa MVVM |
| `System.IO.Ports` | 10.0.10 | MIT | <https://dot.net/> | port COM adaptera slcan |
| `ScottPlot.WPF` | 5.1.59 | MIT | <https://scottplot.net/> | wykresy telemetrii na żywo w modułach |
| `SkiaSharp.Views.WPF` | 4.150.1 | MIT | <https://github.com/mono/SkiaSharp> | backend renderowania dla ScottPlot |

## Pakiety NuGet (przechodnie, trafiają do katalogu wynikowego)

Wciągane przez ScottPlot i SkiaSharp; wymienione, bo ich pliki `.dll`/`.so` znajdują się
w dystrybuowanym pakiecie.

| Pakiet | Wersja | Licencja | Projekt |
|---|---|---|---|
| `SkiaSharp` | 4.150.1 | MIT | <https://github.com/mono/SkiaSharp> |
| `SkiaSharp.HarfBuzz` | 3.119.0 | MIT | <https://github.com/mono/SkiaSharp> |
| `SkiaSharp.Views.Desktop.Common` | 4.150.1 | MIT | <https://github.com/mono/SkiaSharp> |
| `HarfBuzzSharp` | 8.3.1.1 | MIT | <https://github.com/mono/SkiaSharp> |
| `OpenTK.GLWpfControl` | 4.3.3 | MIT | <https://github.com/opentk/GLWpfControl> |
| `OpenTK.*` (Core, Graphics, Mathematics, Windowing.*, Audio.OpenAL, Compute, Input) | 4.9.4 | MIT | <https://github.com/opentk/opentk> |
| `Microsoft.Windows.SDK.NET.Ref` | 10.0.19041.x | MIT | <https://github.com/microsoft/CsWinRT> |

SkiaSharp i HarfBuzzSharp dostarczają natywne biblioteki (`libSkiaSharp.dll`,
`libHarfBuzzSharp.dll`) będące buildami projektów **Skia** (BSD-3-Clause, Google) i
**HarfBuzz** (MIT "Old"). Noty licencyjne tych projektów są dołączone w ich pakietach NuGet.

## Runtime

Aplikacja jest publikowana jako **self-contained** — pakiet zawiera środowisko
uruchomieniowe **.NET 10** (MIT, <https://github.com/dotnet/runtime>). Dzięki temu instalacja
nie wymaga wcześniej zainstalowanego .NET na maszynie docelowej.

## Narzędzia (nie trafiają do binarki)

| Narzędzie | Licencja | Po co |
|---|---|---|
| `vpk` (Velopack CLI) | MIT | składanie instalatora i paczek aktualizacyjnych w CI |
| `xunit`, `xunit.runner.visualstudio` | Apache-2.0 / MIT | testy jednostkowe warstwy Core |
| `Microsoft.NET.Test.Sdk` | MIT | host testów |
| MkDocs + Material for MkDocs | BSD-2-Clause / MIT | budowanie strony dokumentacji |

## Sprzęt i protokół

Aplikacja rozmawia z adapterem **WeActStudio USB2CANFDV1** we firmware
[slcan](https://github.com/WeActStudio/WeActStudio.USB2CANFDV1) przy użyciu poleceń
w formacie **Lawicel SLCAN**. Format ten jest de facto standardem, implementowanym tutaj
od zera na podstawie publicznego opisu poleceń — żaden kod producenta nie jest tu zawarty.
