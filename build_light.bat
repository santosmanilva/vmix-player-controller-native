@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>&1
if errorlevel 1 (
  echo Es necesario .NET SDK 8 para compilar.
  exit /b 1
)

dotnet publish native\vMixPlayerController.csproj -c Release -r win-x64 --self-contained false ^
  -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o dist-light
if errorlevel 1 goto :error

echo.
echo Version ligera creada en dist-light\vMix-Player-Controller.exe
echo Requiere Microsoft .NET 8 Desktop Runtime x64 en el equipo de destino.
exit /b 0

:error
echo.
echo No se pudo crear el ejecutable ligero.
exit /b 1
