@echo off
rem Launch the King's Field III (SLUS-00255) recomp with the disc image.
rem All console output, including a crash's stack trace, goes to run.log.
echo Running... output is being written to run.log
"%~dp0bin\Release\net10.0\KingsFieldIII.exe" "%~dp0disc\KingsFieldIII.cue" > "%~dp0run.log" 2>&1
echo Game exited (code %ERRORLEVEL%). See run.log.
pause
