[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    # Pomija `dotnet build` przed uruchomieniem (zakłada, że binarka jest już aktualna).
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$appProject = Join-Path $PSScriptRoot 'src\CanSensorHub.App\CanSensorHub.App.csproj'

if ($NoBuild) {
    Write-Host "Uruchamianie CanSensorHub.App ($Configuration, bez budowania)..." -ForegroundColor Cyan
    dotnet run --project $appProject -c $Configuration --no-build
}
else {
    Write-Host "Budowanie i uruchamianie CanSensorHub.App ($Configuration)..." -ForegroundColor Cyan
    dotnet run --project $appProject -c $Configuration
}

exit $LASTEXITCODE
