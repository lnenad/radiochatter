param(
    [string]$Configuration = "Release",
    [string]$GameDir = "D:\SteamLibrary\steamapps\common\Nuclear Option\"
)

$ErrorActionPreference = "Stop"
$proj = Join-Path $PSScriptRoot "src\RadioChatter\RadioChatter.csproj"

# dotnet SDK resolves Microsoft.NET.Sdk; net472 reference assemblies come from NuGet.
dotnet build $proj -c $Configuration "-p:GameDir=$GameDir"
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

$outDll = Join-Path $PSScriptRoot "src\RadioChatter\bin\$Configuration\RadioChatter.dll"
$pluginDir = Join-Path $GameDir "BepInEx\plugins\RadioChatter"
New-Item -ItemType Directory -Force $pluginDir | Out-Null
Copy-Item $outDll $pluginDir -Force

$sidecarSrc = Join-Path $PSScriptRoot "sidecar"
$sidecarDir = Join-Path $pluginDir "sidecar"
New-Item -ItemType Directory -Force $sidecarDir | Out-Null
foreach ($name in @("server.py", "requirements.txt", "voices.json", "run_sidecar.bat", "run_sidecar.sh")) {
    $source = Join-Path $sidecarSrc $name
    if (Test-Path -LiteralPath $source) {
        Copy-Item -LiteralPath $source -Destination $sidecarDir -Force
    }
}

Write-Host "Deployed $outDll -> $pluginDir"
Write-Host "Deployed sidecar files -> $sidecarDir"
