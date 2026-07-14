param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$GameDir = "D:\SteamLibrary\steamapps\common\Nuclear Option\",
    [string]$Configuration = "Release",
    [switch]$NoCommit
)

$ErrorActionPreference = "Stop"

if ($Version -notmatch '^v?\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$') {
    throw "Version must look like 0.1.0 or v0.1.0"
}

$tag = if ($Version.StartsWith("v")) { $Version } else { "v$Version" }
$plainVersion = $tag.Substring(1)
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $root "src\RadioChatter\RadioChatter.csproj"
$payload = Join-Path $root "release\payload"

Push-Location $root
try {
    $dirty = git -C $root status --porcelain
    if ($LASTEXITCODE -ne 0) { throw "git status failed" }
    if ($dirty -and -not $NoCommit) {
        throw "Working tree has uncommitted changes. Commit or stash them before creating a release tag, or use -NoCommit."
    }

    Write-Host "Building RadioChatter $plainVersion..."
    dotnet build $project -c $Configuration "-p:GameDir=$GameDir" "-p:Version=$plainVersion"
    if ($LASTEXITCODE -ne 0) { throw "Build failed" }

    if (Test-Path -LiteralPath $payload) {
        Get-ChildItem -LiteralPath $payload -Force | Remove-Item -Recurse -Force
    } else {
        New-Item -ItemType Directory -Force -Path $payload | Out-Null
    }

    # Only the DLL is committed as a prebuilt artifact (CI cannot build it: the
    # project links against game assemblies that exist only on this machine).
    # The sidecar is staged fresh from sidecar/ by package_github_release.py.
    $outDll = Join-Path $root "src\RadioChatter\bin\$Configuration\RadioChatter.dll"
    Copy-Item -LiteralPath $outDll -Destination (Join-Path $payload "RadioChatter.dll") -Force

    if ($NoCommit) {
        Write-Host "Updated release/payload but did not commit or tag because -NoCommit was passed."
        return
    }

    git -C $root add release/payload
    if ($LASTEXITCODE -ne 0) { throw "git add failed" }

    git -C $root commit -m "Prepare $tag release payload"
    if ($LASTEXITCODE -ne 0) { throw "Commit failed" }

    git -C $root tag -a $tag -m "RadioChatter $tag"
    if ($LASTEXITCODE -ne 0) { throw "Tag failed" }

    # Push the branch and the tag as two separate pushes: pushing them together in
    # one command can make GitHub skip the tag push event, so Actions never triggers.
    Write-Host "Created $tag. Push with: git push origin HEAD; git push origin $tag"
}
finally {
    Pop-Location
}
