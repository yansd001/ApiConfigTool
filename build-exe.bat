@echo off
setlocal
cd /d "%~dp0" || exit /b 1
call npm ci
if errorlevel 1 exit /b 1
call npm run build
if errorlevel 1 exit /b 1
rem Recreate generated output so removed documentation and licenses cannot linger.
if exist "%~dp0publish" (
  rmdir /s /q "%~dp0publish"
  if exist "%~dp0publish" exit /b 1
)
mkdir publish
if errorlevel 1 exit /b 1
copy /y src-tauri\target\release\api-config-tool.exe publish\ApiConfigTool.exe >nul
if errorlevel 1 exit /b 1
echo Published: %~dp0publish\ApiConfigTool.exe
