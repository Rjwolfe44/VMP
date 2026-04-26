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

echo Building LmpClient...
if defined MSBUILD_EXE (
  call "%MSBUILD_EXE%" "%~dp0..\LmpClient\LmpClient.csproj" /t:Build "/p:Configuration=%SOLUTIONCONFIGURATION%;Platform=AnyCPU;SolutionDir=%REPOROOT%\\;TargetFrameworkVersion=v4.7.2"
) else (
  call "%DOTNET_CMD%" msbuild "%~dp0..\LmpClient\LmpClient.csproj" /t:Build "/p:Configuration=%SOLUTIONCONFIGURATION%;Platform=AnyCPU;SolutionDir=%REPOROOT%\\;TargetFrameworkVersion=v4.7.2"
)
if errorlevel 1 (
  echo Client build failed.
  exit /b 1
)

echo Deploying client files to KSP...
call "%~dp0CopyToKSPDirectory.bat" %SOLUTIONCONFIGURATION%
if errorlevel 1 (
  echo Client deploy failed.
  exit /b 1
)

echo Client build and deploy completed.
exit /b 0
