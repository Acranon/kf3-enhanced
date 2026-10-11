# Run the port unattended with scripted input, then report how far it got.
#
#   powershell -File scripts\test\autorun.ps1 -Pad "4:Start:150,fdat+20:Circle:150" -Seconds 200 -Name auto1
#
# Writes <Name>.out/.err in tests\logs. If the game is still running at the
# end it prints three stack samples of the game thread (needs dotnet-stack:
# dotnet tool install -g dotnet-stack), then stops it. Like shot.ps1 it runs in
# tests\run, so the player's own save and settings are not touched.
param(
    [string]$Pad = "",
    [int]$Seconds = 180,
    [string]$Name = "auto",
    [string]$Log = ""
)

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$bin = Join-Path $root "bin\Release\net10.0"
$crash = Join-Path $bin "crash.log"
if (Test-Path $crash) { Remove-Item $crash }

$env:KFIII_AUTOPAD = $Pad
$env:KFIII_LOG = $Log
$work = "$root\tests\run"
New-Item -ItemType Directory -Force "$root\tests\logs", $work | Out-Null
$p = Start-Process (Join-Path $bin "KingsFieldIII.exe") -ArgumentList "`"$root\disc\KingsFieldIII.cue`"" `
    -WorkingDirectory $work -RedirectStandardOutput "$root\tests\logs\$Name.out" -RedirectStandardError "$root\tests\logs\$Name.err" -PassThru
$env:KFIII_AUTOPAD = $null
$env:KFIII_LOG = $null

$t = 0
while ($t -lt $Seconds -and -not $p.HasExited) { Start-Sleep 5; $t += 5 }

if (-not $p.HasExited) {
    "still running after ${t}s (cpu $($p.CPU)); game thread samples:"
    $stack = "$env:USERPROFILE\.dotnet\tools\dotnet-stack.exe"
    1..3 | ForEach-Object {
        # dotnet-stack has hung on a busy process; give each sample 20 s.
        $tmp = [IO.Path]::GetTempFileName()
        $s = Start-Process $stack -ArgumentList "report -p $($p.Id)" -RedirectStandardOutput $tmp -PassThru -WindowStyle Hidden
        if (-not $s.WaitForExit(20000)) { Stop-Process $s -Force; "  (sample timed out)" }
        $r = Get-Content $tmp -Raw; Remove-Item $tmp
        ($r -split "Thread \(" | Where-Object { $_ -match 'Program' }) -split "`n" |
            Where-Object { $_ -match 'KingsFieldIII!|Sdk\.' } | Select-Object -First 9 |
            ForEach-Object { "  " + ($_.Trim() -replace '\(class RecompOne.*', '') }
        "  -----"
    }
    Stop-Process $p -Force
} else {
    "exited after ${t}s with code $($p.ExitCode)"
}

"--- milestones"
Select-String -Path "$root\tests\logs\$Name.out" -Pattern 'anchor|loaded overlay|Unhandled' | ForEach-Object { "  " + $_.Line }
if (Test-Path $crash) { "--- crash.log"; Get-Content $crash | Select-Object -First 8 }
