# Load every save state in tests\saves, one run each, and report which ones fail.
#
#   powershell -File scripts\test\all_saves.ps1                  # all of them
#   powershell -File scripts\test\all_saves.ps1 -Filter lamp*    # some of them
#
# Each run quick-loads the save from the title (KFIII_TEST_QUICK=load@4), lets the
# world run for a few seconds, takes a screenshot (tests\shots\all_<save>_<t>.png)
# and checks the log: the load must succeed, and no exception or crash.log may
# appear. Runs go through shot.ps1, so they use tests\run, never the player's files.
# The game window takes over the screen while this runs.
param(
    [string]$Filter = "*",
    [double]$Shot = 10,
    [int]$Seconds = 12,
    [string]$Exe = ""
)
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $Exe) { $Exe = "$root\bin\Release\net10.0\KingsFieldIII.exe" }
$crash = Join-Path (Split-Path -Parent $Exe) "crash.log"
$saves = Get-ChildItem "$root\tests\saves\$Filter.kfstate" | Sort-Object Name
$results = @()
$i = 0
foreach ($s in $saves) {
    $i++
    $name = $s.BaseName
    if (Test-Path $crash) { Remove-Item $crash }
    & "$PSScriptRoot\shot.ps1" -Name "all_$name" -Save $name -At $Shot -Seconds $Seconds -Exe $Exe `
        -Env @{ KFIII_TEST_QUICK = "load@4" }
    $out = Get-Content "$root\tests\logs\all_$name.out" -Raw -ErrorAction SilentlyContinue
    $err = Get-Content "$root\tests\logs\all_$name.err" -Raw -ErrorAction SilentlyContinue
    $problems = @()
    if ($out -notmatch "quick loaded") { $problems += "no load" }
    if ($out -match "quick load failed") { $problems += "load failed" }
    if (($out + $err) -match "Unhandled|Exception") { $problems += "exception" }
    if (Test-Path $crash) { $problems += "crash.log" }
    $area = if ($out -match "quick loaded \((fdat\d+)\)") { $Matches[1] } else { "" }
    $status = if ($problems.Count) { "FAIL: " + ($problems -join ", ") } else { "ok" }
    "{0,3}/{1}  {2,-28} {3,-7} {4}" -f $i, $saves.Count, $name, $area, $status
    $results += [pscustomobject]@{ Save = $name; Area = $area; Status = $status }
}
$results | Export-Csv "$root\tests\logs\all_saves.csv" -NoTypeInformation
$bad = @($results | Where-Object { $_.Status -ne "ok" })
""
"{0} of {1} loaded cleanly; summary in tests\logs\all_saves.csv" -f ($results.Count - $bad.Count), $results.Count
