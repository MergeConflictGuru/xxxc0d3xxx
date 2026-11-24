@echo off
setlocal
cd /d "%~dp0"
echo Repair the Windows sandbox for the aiaiai Codex account.
echo Windows administrator approval may be required.
where py >nul 2>nul
if not errorlevel 1 (
  py -3 "%~dp0repair_codex_sandbox.py" --user aiaiai --pause
) else (
  python "%~dp0repair_codex_sandbox.py" --user aiaiai --pause
)
