[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    # Usuwa foldery bin/obj przed budowaniem (pełny, czysty build).
    [switch]$Clean,

    # dotnet publish (samodzielny, gotowy do dystrybucji katalog) zamiast dotnet build.
    [switch]$Publish,

    [string]$Runtime = 'win-x64',

    # Pomija narzędzia serwisowe (zestawy CanSensorHub.Tools.*) - aplikacja bez menu "Narzędzia".
    [switch]$NoTools
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$sln = Join-Path $PSScriptRoot 'CanSensorHub.slnx'
$toolsArg = "-p:WithTools=$(if ($NoTools) { 'false' } else { 'true' })"

if ($Clean) {
    Write-Host "Czyszczenie folderów bin/obj..." -ForegroundColor Cyan
    Get-ChildItem -Path $PSScriptRoot -Include 'bin', 'obj' -Recurse -Directory |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

if ($Publish) {
    $appProject = Join-Path $PSScriptRoot 'src\CanSensorHub.App\CanSensorHub.App.csproj'
    $outDir = Join-Path $PSScriptRoot "publish\$Configuration"
    Write-Host "Publikowanie CanSensorHub.App ($Configuration, $Runtime) -> $outDir" -ForegroundColor Cyan
    dotnet publish $appProject -c $Configuration -r $Runtime --self-contained false -o $outDir $toolsArg
}
else {
    Write-Host "Budowanie rozwiązania CanSensorHub ($Configuration)..." -ForegroundColor Cyan
    dotnet build $sln -c $Configuration $toolsArg
}

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build nie powiódł się (kod $LASTEXITCODE)." -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "Gotowe." -ForegroundColor Green
