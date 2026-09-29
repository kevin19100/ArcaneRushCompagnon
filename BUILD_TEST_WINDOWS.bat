@echo off
setlocal EnableExtensions
cd /d "%~dp0"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"

set "DOTNET_EXE="
where dotnet >nul 2>nul
if not errorlevel 1 (
  for /f "delims=" %%D in ('where dotnet 2^>nul') do if not defined DOTNET_EXE set "DOTNET_EXE=%%D"
)

if defined DOTNET_EXE (
  "%DOTNET_EXE%" --list-sdks 2>nul | findstr /R /B "10\." >nul
  if errorlevel 1 set "DOTNET_EXE="
)

if not defined DOTNET_EXE (
  if exist "%ProgramFiles%\dotnet\dotnet.exe" (
    "%ProgramFiles%\dotnet\dotnet.exe" --list-sdks 2>nul | findstr /R /B "10\." >nul
    if not errorlevel 1 set "DOTNET_EXE=%ProgramFiles%\dotnet\dotnet.exe"
  )
)

if not defined DOTNET_EXE (
  echo [Arcane Rush Sync] SDK .NET 10 introuvable.
  where winget >nul 2>nul
  if errorlevel 1 (
    echo.
    echo Installe le SDK .NET 10 puis relance ce fichier :
    echo https://dotnet.microsoft.com/download/dotnet/10.0
    pause
    exit /b 1
  )

  echo Installation du SDK .NET 10 via winget...
  winget install --id Microsoft.DotNet.SDK.10 --exact --source winget --accept-package-agreements --accept-source-agreements
  if errorlevel 1 (
    echo.
    echo Installation impossible. Installe .NET 10 manuellement puis relance.
    pause
    exit /b 1
  )

  if exist "%ProgramFiles%\dotnet\dotnet.exe" (
    set "DOTNET_EXE=%ProgramFiles%\dotnet\dotnet.exe"
    set "PATH=%ProgramFiles%\dotnet;%PATH%"
  )
)

if not defined DOTNET_EXE (
  echo.
  echo .NET 10 semble installe mais dotnet.exe reste introuvable.
  echo Ferme cette fenetre puis relance BUILD_TEST_WINDOWS.bat.
  pause
  exit /b 1
)

"%DOTNET_EXE%" --list-sdks 2>nul | findstr /R /B "10\." >nul
if errorlevel 1 (
  echo.
  echo Le SDK .NET 10 n'est pas disponible. Relance ce fichier apres l'installation.
  pause
  exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\build-test-windows.ps1" -DotNetExe "%DOTNET_EXE%"
if errorlevel 1 (
  echo.
  echo ECHEC DU BUILD. Copie le message affiche ci-dessus pour le diagnostic.
  pause
  exit /b 1
)

echo.
echo ===============================================================
echo  BUILD OK - ARCANE RUSH SYNC
echo ===============================================================
echo Le dossier artifacts\win-x64 va s'ouvrir.
echo Lance ensuite ArcaneRushSync.exe.
start "" "%~dp0artifacts\win-x64"
pause
