@echo off
setlocal EnableExtensions DisableDelayedExpansion
chcp 65001 >nul
title Kotoshiro - Build Installer

set "KOTOSHIRO_PS="
for /f "delims=" %%P in ('where pwsh.exe 2^>nul') do if not defined KOTOSHIRO_PS set "KOTOSHIRO_PS=%%P"
if not defined KOTOSHIRO_PS if exist "%ProgramFiles%\PowerShell\7\pwsh.exe" set "KOTOSHIRO_PS=%ProgramFiles%\PowerShell\7\pwsh.exe"
if not defined KOTOSHIRO_PS if exist "%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies\native\powershell\pwsh.exe" set "KOTOSHIRO_PS=%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies\native\powershell\pwsh.exe"
if not defined KOTOSHIRO_PS set "KOTOSHIRO_PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"

"%KOTOSHIRO_PS%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Update-Runtime.ps1" %*
set "KOTOSHIRO_RESULT=%ERRORLEVEL%"
if not "%KOTOSHIRO_RESULT%"=="0" echo Build failed. See the message above.
if not defined KOTOSHIRO_BUILD_NO_PAUSE pause
exit /b %KOTOSHIRO_RESULT%
