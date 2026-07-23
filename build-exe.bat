@echo off
cd /d "%~dp0"
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
echo.
echo Published: %~dp0publish\ApiConfigTool.exe
pause
