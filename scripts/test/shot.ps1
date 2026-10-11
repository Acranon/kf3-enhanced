# Run the game unattended with environment switches, capture its window at given
# times, then stop it.
#
#   powershell -File scripts\test\shot.ps1 -Name throne -Save throne -At 8,12 -Seconds 13 `
#       -Env @{KFIII_TEST_QUICK="load@4"}
#
# The game runs in its own working directory (tests\run by default), so it reads and
# writes that folder's quicksave.kfstate, settings and map memory, never the
# player's. -Save copies tests\saves\<name>.kfstate there first; KFIII_TEST_QUICK
# then loads it. -Exe runs another build (for example a compile check built with
# -o into a scratch folder while the normal build is in use).
#
# Logs go to tests\logs\<Name>.out/.err, screenshots to tests\shots\<Name>_<t>.png,
# where t is in tenths of a second (milliseconds with -Millis). -ClickAt clicks the
# left mouse button at window position (-CX, -CY) at that time. Windows only:
# PrintWindow captures the game window even when another window covers it.
param(
    [string]$Name,
    [double[]]$At = @(),
    [int]$Seconds = 30,
    [hashtable]$Env = @{},
    [string]$Save = "",
    [string]$Exe = "",
    [string]$WorkDir = "",
    [switch]$Millis,
    [double]$ClickAt = -1,
    [int]$CX = 0,
    [int]$CY = 0
)
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class W { [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Ri, B; }
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, IntPtr e); }
"@
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $Exe) { $Exe = "$root\bin\Release\net10.0\KingsFieldIII.exe" }
if (-not $WorkDir) { $WorkDir = "$root\tests\run" }
$logs = "$root\tests\logs"; $shots = "$root\tests\shots"
New-Item -ItemType Directory -Force $logs, $shots, $WorkDir | Out-Null
if ($Save) { Copy-Item "$root\tests\saves\$Save.kfstate" "$WorkDir\quicksave.kfstate" -Force }

foreach ($k in $Env.Keys) { [Environment]::SetEnvironmentVariable($k, $Env[$k]) }
$p = Start-Process $Exe -ArgumentList "`"$root\disc\KingsFieldIII.cue`"" -WorkingDirectory $WorkDir `
    -RedirectStandardOutput "$logs\$Name.out" -RedirectStandardError "$logs\$Name.err" -PassThru
foreach ($k in $Env.Keys) { [Environment]::SetEnvironmentVariable($k, $null) }

function Wait-Until([double]$t) {
    while (((Get-Date) - $p.StartTime).TotalSeconds -lt $t) { [Threading.Thread]::Sleep(20) }
}

if ($ClickAt -ge 0) {
    Wait-Until $ClickAt
    $p.Refresh(); $h = $p.MainWindowHandle; [void][W]::SetForegroundWindow($h)
    $r = New-Object W+R; [void][W]::GetWindowRect($h, [ref]$r)
    [void][W]::SetCursorPos($r.L + $CX, $r.T + $CY); [Threading.Thread]::Sleep(300)
    [W]::mouse_event(2, 0, 0, 0, [IntPtr]::Zero); [Threading.Thread]::Sleep(60)
    [W]::mouse_event(4, 0, 0, 0, [IntPtr]::Zero)
}

foreach ($t in $At) {
    Wait-Until $t
    $p.Refresh(); $h = $p.MainWindowHandle
    if ($h -eq [IntPtr]::Zero) { Write-Host "no window at ${t}s"; continue }
    $r = New-Object W+R; [void][W]::GetWindowRect($h, [ref]$r)
    $bmp = New-Object System.Drawing.Bitmap ($r.Ri - $r.L), ($r.B - $r.T)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc()
    [void][W]::PrintWindow($h, $dc, 2); $g.ReleaseHdc($dc)
    $stamp = if ($Millis) { [int]($t * 1000) } else { [int]($t * 10) }
    $bmp.Save("$shots\${Name}_$stamp.png"); $g.Dispose(); $bmp.Dispose()
}

while (((Get-Date) - $p.StartTime).TotalSeconds -lt $Seconds -and -not $p.HasExited) { [Threading.Thread]::Sleep(200) }
# The whole tree: a release's stub starts the game as a child process.
if (-not $p.HasExited) { & taskkill /F /T /PID $p.Id | Out-Null }
