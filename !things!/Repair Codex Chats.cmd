@echo off
setlocal
cd /d "%~dp0"
echo Codex chat repair
echo Rebuilds missing rollout files after transferring the Codex databases.
echo Checks the Codex command runner after restoring chats.
echo For sandbox setup errors, use the adjacent Repair Codex Sandbox.cmd.
echo.
where py >nul 2>nul
if not errorlevel 1 (
  py -3 "%~dp0repair_codex_chats.py"
) else (
  python "%~dp0repair_codex_chats.py"
)
if errorlevel 1 echo Repair failed. Read the error above.
echo.
pause
