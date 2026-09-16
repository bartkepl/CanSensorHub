[CmdletBinding()]
param(
    [string]$Runtime = 'win-x64',

    # Samodzielny katalog z dołączonym runtime .NET (nie wymaga zainstalowanego .NET na maszynie docelowej).
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$appProject = Join-Path $PSScriptRoot 'src\CanSensorHub.App\CanSensorHub.App.csproj'
$outDir = Join-Path $PSScriptRoot 'build'

# Katalog build/ leży na wierzchu repo (obok build.ps1), zamiast bin/obj zagrzebanych w plikach
# projektowych — jeden, przewidywalny adres na gotową do uruchomienia wersję Release.
if (Test-Path $outDir) {
    Write-Host "Czyszczenie poprzedniej zawartości build/..." -ForegroundColor Cyan
    Remove-Item -Path $outDir -Recurse -Force
}

Write-Host "Publikowanie CanSensorHub.App (Release, $Runtime, self-contained=$($SelfContained.IsPresent)) -> $outDir" -ForegroundColor Cyan
dotnet publish $appProject -c Release -r $Runtime --self-contained $SelfContained.IsPresent -o $outDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build nie powiódł się (kod $LASTEXITCODE)." -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "Gotowe: $outDir\CanSensorHub.App.exe" -ForegroundColor Green
