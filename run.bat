@echo off
setlocal
chcp 65001 >nul
title Kotoshiro - Build runtime

set "KOTOSHIRO_PS="
where pwsh.exe >nul 2>&1
if not errorlevel 1 set "KOTOSHIRO_PS=pwsh.exe"
if not defined KOTOSHIRO_PS if exist "%ProgramFiles%\PowerShell\7\pwsh.exe" set "KOTOSHIRO_PS=%ProgramFiles%\PowerShell\7\pwsh.exe"
if not defined KOTOSHIRO_PS if exist "%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies\native\powershell\pwsh.exe" set "KOTOSHIRO_PS=%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies\native\powershell\pwsh.exe"
if not defined KOTOSHIRO_PS set "KOTOSHIRO_PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"

"%KOTOSHIRO_PS%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Update-Runtime.ps1" %*
set "KOTOSHIRO_EXIT=%ERRORLEVEL%"
echo.
if not "%KOTOSHIRO_EXIT%"=="0" echo Build failed. See the message above.
if not defined KOTOSHIRO_BUILD_NO_PAUSE pause
exit /b %KOTOSHIRO_EXIT%
