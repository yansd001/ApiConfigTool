@echo off
setlocal
cd /d "%~dp0"
call npm ci
if errorlevel 1 exit /b 1
call npm run build
if errorlevel 1 exit /b 1
if not exist publish mkdir publish
copy /y src-tauri\target\release\api-config-tool.exe publish\ApiConfigTool.exe >nul
if errorlevel 1 exit /b 1
if not exist publish\licenses mkdir publish\licenses
copy /y licenses\*.txt publish\licenses\ >nul
copy /y README.md publish\README.md >nul
xcopy /e /i /y docs publish\docs >nul
echo Published: %~dp0publish\ApiConfigTool.exe
