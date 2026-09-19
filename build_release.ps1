[CmdletBinding()]
param(
    [string]$Runtime = 'win-x64',

    # Samodzielny katalog z dołączonym runtime .NET (nie wymaga zainstalowanego .NET
    # na maszynie docelowej). Przełącznik -Pack wymusza ten tryb.
    [switch]$SelfContained,

    # Zbuduj instalator i paczki aktualizacyjne Velopack, tak jak robi to CI.
    # Wynik ląduje w artifacts\releases (CanSensorHub-win-Setup.exe i reszta).
    [switch]$Pack,

    # Wersja pakietu. Domyślnie MAJOR.MINOR z pliku VERSION + ".0" — numer poprawki
    # w prawdziwych wydaniach pochodzi z numeru przebiegu GitHub Actions.
    [string]$Version
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$appProject = Join-Path $PSScriptRoot 'src\CanSensorHub.App\CanSensorHub.App.csproj'
$outDir = Join-Path $PSScriptRoot 'build'

if (-not $Version) {
    $Version = "$((Get-Content (Join-Path $PSScriptRoot 'VERSION') -Raw).Trim()).0"
}

# Instalator Velopack musi nieść własny runtime — inaczej aktualizacja trafiłaby
# na maszynę bez .NET i aplikacja nie wstałaby po restarcie.
$selfContainedEffective = $SelfContained.IsPresent -or $Pack.IsPresent

# Katalog build/ leży na wierzchu repo (obok build.ps1), zamiast bin/obj zagrzebanych w plikach
# projektowych — jeden, przewidywalny adres na gotową do uruchomienia wersję Release.
if (Test-Path $outDir) {
    Write-Host "Czyszczenie poprzedniej zawartości build/..." -ForegroundColor Cyan
    Remove-Item -Path $outDir -Recurse -Force
}

Write-Host "Publikowanie CanSensorHub.App (Release $Version, $Runtime, self-contained=$selfContainedEffective) -> $outDir" -ForegroundColor Cyan
dotnet publish $appProject -c Release -r $Runtime --self-contained $selfContainedEffective -o $outDir -p:Version=$Version

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build nie powiódł się (kod $LASTEXITCODE)." -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "Gotowe: $outDir\CanSensorHub.exe" -ForegroundColor Green

if (-not $Pack) { return }

# ── Velopack ──────────────────────────────────────────────────────────────────
$releasesDir = Join-Path $PSScriptRoot 'artifacts\releases'
$icon = Join-Path $PSScriptRoot 'src\CanSensorHub.App\Resources\CanSensorHub.ico'

Write-Host ""
Write-Host "Przywracanie narzędzi lokalnych (vpk)..." -ForegroundColor Cyan
dotnet tool restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Pakowanie Velopack $Version -> $releasesDir" -ForegroundColor Cyan
dotnet vpk pack `
    --packId CanSensorHub `
    --packVersion $Version `
    --packDir $outDir `
    --mainExe CanSensorHub.exe `
    --packTitle 'CanSensorHub' `
    --packAuthors 'bartkepl' `
    --icon $icon `
    --outputDir $releasesDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "Pakowanie nie powiodło się (kod $LASTEXITCODE)." -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "Gotowe: $releasesDir\CanSensorHub-win-Setup.exe" -ForegroundColor Green
Write-Host "Uwaga: aktualizacja z lokalnie zbudowanej paczki nie zadziała — klient" -ForegroundColor Yellow
Write-Host "szuka wydań w repozytorium GitHub, nie w tym katalogu." -ForegroundColor Yellow
