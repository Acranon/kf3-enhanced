# Build the Windows x64 release: a portable zip and, with Inno Setup installed, an
# installer.
#
#   powershell -ExecutionPolicy Bypass -File packaging\windows\build-windows.ps1
#
# Needs the .NET 10 SDK. It does NOT need the disc and ships nothing from it: the
# launcher carries config/ and the port's own C#, and builds the game on the
# player's machine from their disc on first launch (see docs/packaging.md).
#
# Output in dist\:
#   KF3Enhanced-<version>-win-x64.zip          portable: unzip anywhere and run
#   KF3Enhanced-<version>-win-x64-setup.exe    installer (needs iscc, Inno Setup 6)
#
# Layout of both: KF3Enhanced.exe (a small stub) + bin\ (the self-contained runtime
# and the real executable) + content\ (config and sources) + licenses\.
$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist 'win-x64'
$version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
if (-not $version) { throw "VERSION is empty" }

Write-Host "==> publishing launcher ($version)"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
$bin = Join-Path $stage 'bin'
dotnet publish (Join-Path $root 'launcher\KF3Enhanced.csproj') -c Release -r win-x64 --self-contained `
    -p:DebugType=none -p:DebugSymbols=false -o $bin
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

# content\ beside the stub, not among the runtime's DLLs.
$content = Join-Path $bin 'content'
if (-not (Test-Path $content)) { throw "publish did not stage content\" }
Move-Item $content (Join-Path $stage 'content')

# Nothing derived from the disc may be in the package. The launcher project cannot
# include generated\, but check rather than trust.
$gameFiles = 'OPEN.EXE', 'GAME.EXE', 'END.EXE', 'SLUS_002.55', 'FDAT.T', 'ITEM.T'
$leak = Get-ChildItem $stage -Recurse -File | Where-Object {
    $_.FullName -match '\\generated\\' -or
    $_.Name -cmatch '^(fdat\d+|game|open|end|main|Entry|Stubs)\.cs$' -or
    $_.Name -in $gameFiles -or
    $_.Extension -in '.kfstate', '.sav', '.bin', '.cue', '.chd'
}
if ($leak) { throw "game-derived files in the package: $($leak.FullName -join ', ')" }

Write-Host "==> stub"
$stubOut = Join-Path $dist 'stub'
if (Test-Path $stubOut) { Remove-Item -Recurse -Force $stubOut }
dotnet publish (Join-Path $PSScriptRoot 'Stub\KF3Enhanced.Stub.csproj') -c Release -o $stubOut
if ($LASTEXITCODE -ne 0) { throw "stub publish failed" }
Copy-Item (Join-Path $stubOut 'KF3Enhanced.exe') (Join-Path $stage 'KF3Enhanced.exe')
$stubConfig = Join-Path $stubOut 'KF3Enhanced.exe.config'
if (Test-Path $stubConfig) { Copy-Item $stubConfig (Join-Path $stage 'KF3Enhanced.exe.config') }
Remove-Item -Recurse -Force $stubOut

Write-Host "==> licences"
$licenses = Join-Path $stage 'licenses'
New-Item -ItemType Directory -Force -Path $licenses | Out-Null
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $licenses 'kf3-enhanced-CC0.txt')
Copy-Item (Join-Path $root 'tools\RecompOne\LICENSE') (Join-Path $licenses 'RecompOne-MIT.txt')
Copy-Item (Join-Path $root 'packaging\licenses\*') $licenses
Copy-Item (Join-Path $root 'README.md') (Join-Path $stage 'README.md')

$zip = Join-Path $dist "KF3Enhanced-$version-win-x64.zip"
Write-Host "==> $zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path "$stage\*" -DestinationPath $zip

$iscc = (Get-Command iscc -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    $iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
              "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if ($iscc) {
    Write-Host "==> installer"
    $env:KF3_VERSION = $version
    & $iscc (Join-Path $PSScriptRoot 'kf3-enhanced.iss')
    if ($LASTEXITCODE -ne 0) { throw "iscc failed" }
} else {
    Write-Host "==> Inno Setup not found; skipping the installer (the zip is built)"
}
Write-Host "done."
