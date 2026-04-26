@echo off
setlocal

if exist "%~dp0SetDirectories.bat" call "%~dp0SetDirectories.bat"

for %%I in ("%~dp0..") do set "REPOROOT=%%~fI"

if "%~1"=="" (
  set SOLUTIONCONFIGURATION=Debug
) else (
  set SOLUTIONCONFIGURATION=%~1
)

if /I not "%SOLUTIONCONFIGURATION%"=="Debug" if /I not "%SOLUTIONCONFIGURATION%"=="Release" (
  echo Invalid configuration "%SOLUTIONCONFIGURATION%". Use Debug or Release.
  exit /b 1
)

if defined DOTNET_EXE (
  set "DOTNET_CMD=%DOTNET_EXE%"
) else (
  set "DOTNET_CMD=dotnet"
)

set "BUILD_ROOT=%REPOROOT%\Build\%SOLUTIONCONFIGURATION%"
set "CLIENT_OUT=%BUILD_ROOT%\Client"
set "SERVER_OUT=%BUILD_ROOT%\Server"
set "USER_LOADING_OUT=%BUILD_ROOT%\UserLoadingScreens"

echo.
echo ===== Building Client =====
if defined MSBUILD_EXE (
  call "%MSBUILD_EXE%" "%~dp0..\LmpClient\LmpClient.csproj" /t:Build "/p:Configuration=%SOLUTIONCONFIGURATION%;Platform=AnyCPU;SolutionDir=%REPOROOT%\\;TargetFrameworkVersion=v4.7.2;RunPostBuildEvent=Never;PostBuildEvent="
) else (
  call "%DOTNET_CMD%" msbuild "%~dp0..\LmpClient\LmpClient.csproj" /t:Build "/p:Configuration=%SOLUTIONCONFIGURATION%;Platform=AnyCPU;SolutionDir=%REPOROOT%\\;TargetFrameworkVersion=v4.7.2;RunPostBuildEvent=Never;PostBuildEvent="
)
if errorlevel 1 (
  echo Client build failed.
  exit /b 1
)

echo.
echo ===== Staging Client mod layout under Build\... =====
if exist "%CLIENT_OUT%" rmdir /s /q "%CLIENT_OUT%"
if exist "%USER_LOADING_OUT%" rmdir /s /q "%USER_LOADING_OUT%"
mkdir "%CLIENT_OUT%\Plugins"
mkdir "%CLIENT_OUT%\Button"
mkdir "%CLIENT_OUT%\Localization"
mkdir "%CLIENT_OUT%\PartSync"
mkdir "%CLIENT_OUT%\Icons"
mkdir "%CLIENT_OUT%\Flags"
mkdir "%CLIENT_OUT%\LoadingScreens"
mkdir "%USER_LOADING_OUT%"

xcopy /Y "%REPOROOT%\LmpClient\bin\%SOLUTIONCONFIGURATION%\*.*" "%CLIENT_OUT%\Plugins\"
xcopy /Y "%REPOROOT%\External\Dependencies\*.*" "%CLIENT_OUT%\Plugins\"
xcopy /Y "%REPOROOT%\LmpClient\Resources\*.png" "%CLIENT_OUT%\Button\"
xcopy /Y /S "%REPOROOT%\LmpClient\Localization\XML\*.*" "%CLIENT_OUT%\Localization\"
xcopy /Y /S "%REPOROOT%\LmpClient\ModuleStore\XML\*.xml" "%CLIENT_OUT%\PartSync\"
xcopy /Y "%REPOROOT%\LmpClient\Resources\Icons\*.*" "%CLIENT_OUT%\Icons\"
xcopy /Y "%REPOROOT%\LmpClient\Resources\Flags\*.*" "%CLIENT_OUT%\Flags\"
xcopy /Y "%REPOROOT%\LmpClient\Resources\LoadingScreens\*.*" "%CLIENT_OUT%\LoadingScreens\"
xcopy /Y "%REPOROOT%\LmpClient\Resources\LoadingScreens\VMPLoadingScreen.png" "%USER_LOADING_OUT%\"

REM KSP / AVC uses ModName.version at GameData\VladMultiplayer\ root (VladMultiplayer.version).
if not exist "%REPOROOT%\VladMultiplayer.version" (
  echo ERROR: VladMultiplayer.version is missing at repo root. It is required for GameData\VladMultiplayer\.
  exit /b 1
)
copy /Y "%REPOROOT%\VladMultiplayer.version" "%CLIENT_OUT%\VladMultiplayer.version" >nul
if not exist "%CLIENT_OUT%\VladMultiplayer.version" (
  echo ERROR: Failed to copy VladMultiplayer.version into Build\...\Client\.
  exit /b 1
)
echo Staged VladMultiplayer.version into Client\ ^(merge Client\ into KSP GameData\VladMultiplayer\^).

if /I "%COPYHARMONY%"=="true" (
  echo COPYHARMONY=true: also staging Harmony under Build\...\000_Harmony\ ^(copy next to VladMultiplayer under GameData^)
  if exist "%BUILD_ROOT%\000_Harmony" rmdir /s /q "%BUILD_ROOT%\000_Harmony"
  xcopy /Y /s /e "%REPOROOT%\External\Dependencies\Harmony\" "%BUILD_ROOT%\000_Harmony\"
)

echo.
echo ===== Publishing Server =====
if exist "%SERVER_OUT%" rmdir /s /q "%SERVER_OUT%"
mkdir "%SERVER_OUT%"
call "%DOTNET_CMD%" publish "%~dp0..\Server\Server.csproj" -c %SOLUTIONCONFIGURATION% -o "%SERVER_OUT%"
if errorlevel 1 (
  echo Server publish failed.
  exit /b 1
)

echo.
echo ===== Build Only Complete =====
echo KSP deployment skipped. Use Build\%SOLUTIONCONFIGURATION%\Client as GameData\VladMultiplayer
echo   ^(VladMultiplayer.version + Plugins, Button, Localization, PartSync, Icons, Flags^).
echo Copy Build\%SOLUTIONCONFIGURATION%\UserLoadingScreens into the KSP root UserLoadingScreens folder for startup loading art.
echo Harmony: use GameData\000_Harmony from KSP, or run with COPYHARMONY=true to also emit Build\...\000_Harmony\.
echo Server runtime: %SERVER_OUT%

exit /b 0
