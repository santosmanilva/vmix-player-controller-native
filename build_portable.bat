@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>&1
if errorlevel 1 (
  echo Es necesario .NET SDK 8 para compilar. El usuario final no necesita instalarlo.
  exit /b 1
)

dotnet publish native\vMixPlayerController.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
if errorlevel 1 goto :error

echo.
echo Portable creado en dist\vMix-Player-Controller.exe
exit /b 0

:error
echo.
echo No se pudo crear el ejecutable portable.
exit /b 1
