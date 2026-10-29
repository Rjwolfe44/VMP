::You must keep this file in the solution folder for it to work.
::Make sure to pass the solution configuration when calling it (either Debug or Release)

::Set the directories in the SetDirectories.bat file if you want a different folder than Kerbal Space Program
::EXAMPLE:
:: SET KSPPATH=C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program
:: SET KSPPATH2=C:\Users\Malte\Desktop\Kerbal Space Program
if exist "%~dp0SetDirectories.bat" call "%~dp0SetDirectories.bat"

IF DEFINED KSPPATH (ECHO KSPPATH is defined) ELSE (SET KSPPATH=C:\Kerbal Space Program)
IF DEFINED KSPPATH2 (ECHO KSPPATH2 is defined)

IF "%~1"=="" (
  SET SOLUTIONCONFIGURATION=Debug
) ELSE (
  SET SOLUTIONCONFIGURATION=%~1
)

IF /I NOT "%SOLUTIONCONFIGURATION%"=="Debug" IF /I NOT "%SOLUTIONCONFIGURATION%"=="Release" (
  ECHO Invalid configuration "%SOLUTIONCONFIGURATION%". Use Debug or Release.
  EXIT /B 1
)

IF NOT DEFINED COPYHARMONY SET COPYHARMONY=false

ECHO Using configuration: %SOLUTIONCONFIGURATION%
ECHO COPYHARMONY=%COPYHARMONY%

mkdir "%KSPPATH%\GameData\VladMultiplayer\"
IF DEFINED KSPPATH2 (mkdir "%KSPPATH2%\GameData\VladMultiplayer\")

mkdir "%KSPPATH%\GameData\VladMultiplayer\Plugins"
IF DEFINED KSPPATH2 (mkdir "%KSPPATH2%\GameData\VladMultiplayer\Plugins")

del "%KSPPATH%\GameData\VladMultiplayer\Plugins\*.*" /Q /F
IF DEFINED KSPPATH2 (del "%KSPPATH2%\GameData\VladMultiplayer\Plugins\*.*" /Q /F)

mkdir "%KSPPATH%\GameData\VladMultiplayer\Button"
IF DEFINED KSPPATH2 (mkdir "%KSPPATH2%\GameData\VladMultiplayer\Button")

del "%KSPPATH%\GameData\VladMultiplayer\Button\*.*" /Q /F
IF DEFINED KSPPATH2 (del "%KSPPATH2%\GameData\VladMultiplayer\Button\*.*" /Q /F)

mkdir "%KSPPATH%\GameData\VladMultiplayer\Localization"
IF DEFINED KSPPATH2 (mkdir "%KSPPATH2%\GameData\VladMultiplayer\Localization")

del "%KSPPATH%\GameData\VladMultiplayer\Localization\*.*" /Q /F
IF DEFINED KSPPATH2 (del "%KSPPATH2%\GameData\VladMultiplayer\Localization\*.*" /Q /F)

mkdir "%KSPPATH%\GameData\VladMultiplayer\PartSync"
IF DEFINED KSPPATH2 (mkdir "%KSPPATH2%\GameData\VladMultiplayer\PartSync")

del "%KSPPATH%\GameData\VladMultiplayer\PartSync\*.*" /Q /F
IF DEFINED KSPPATH2 (del "%KSPPATH2%\GameData\VladMultiplayer\PartSync\*.*" /Q /F)

mkdir "%KSPPATH%\GameData\VladMultiplayer\Icons"
IF DEFINED KSPPATH2 (mkdir "%KSPPATH2%\GameData\VladMultiplayer\Icons")

del "%KSPPATH%\GameData\VladMultiplayer\Icons\*.*" /Q /F
IF DEFINED KSPPATH2 (del "%KSPPATH2%\GameData\VladMultiplayer\Icons\*.*" /Q /F)

mkdir "%KSPPATH%\GameData\VladMultiplayer\Flags"
IF DEFINED KSPPATH2 (mkdir "%KSPPATH2%\GameData\VladMultiplayer\Flags")

del "%KSPPATH%\GameData\VladMultiplayer\Flags\*.*" /Q /F
IF DEFINED KSPPATH2 (del "%KSPPATH2%\GameData\VladMultiplayer\Flags\*.*" /Q /F)

mkdir "%KSPPATH%\GameData\VladMultiplayer\LoadingScreens"
IF DEFINED KSPPATH2 (mkdir "%KSPPATH2%\GameData\VladMultiplayer\LoadingScreens")

del "%KSPPATH%\GameData\VladMultiplayer\LoadingScreens\*.*" /Q /F
IF DEFINED KSPPATH2 (del "%KSPPATH2%\GameData\VladMultiplayer\LoadingScreens\*.*" /Q /F)

mkdir "%KSPPATH%\UserLoadingScreens"
IF DEFINED KSPPATH2 (mkdir "%KSPPATH2%\UserLoadingScreens")

IF /I "%COPYHARMONY%"=="true" (
  xcopy /Y /s /e "%~dp0..\External\Dependencies\Harmony\" "%KSPPATH%\GameData\"
  IF DEFINED KSPPATH2 (xcopy /Y /s /e "%~dp0..\External\Dependencies\Harmony\" "%KSPPATH2%\GameData\")
) ELSE (
  ECHO Skipping Harmony copy. Existing 000_Harmony will be left untouched.
)

IF NOT EXIST "%~dp0..\VladMultiplayer.version" (
  ECHO ERROR: VladMultiplayer.version missing at repo root.
  EXIT /B 1
)
copy /Y "%~dp0..\VladMultiplayer.version" "%KSPPATH%\GameData\VladMultiplayer\VladMultiplayer.version" >nul
IF DEFINED KSPPATH2 (copy /Y "%~dp0..\VladMultiplayer.version" "%KSPPATH2%\GameData\VladMultiplayer\VladMultiplayer.version" >nul)

xcopy /Y "%~dp0..\LmpClient\bin\%SOLUTIONCONFIGURATION%\*.*" "%KSPPATH%\GameData\VladMultiplayer\Plugins"
IF DEFINED KSPPATH2 (xcopy /Y "%~dp0..\LmpClient\bin\%SOLUTIONCONFIGURATION%\*.*" "%KSPPATH2%\GameData\VladMultiplayer\Plugins")

xcopy /Y "%~dp0..\External\Dependencies\*.*" "%KSPPATH%\GameData\VladMultiplayer\Plugins"
IF DEFINED KSPPATH2 (xcopy /Y "%~dp0..\External\Dependencies\*.*" "%KSPPATH2%\GameData\VladMultiplayer\Plugins")

xcopy /Y "%~dp0..\LmpClient\Resources\*.png" "%KSPPATH%\GameData\VladMultiplayer\Button"
IF DEFINED KSPPATH2 (xcopy /Y "%~dp0..\LmpClient\Resources\*.png" "%KSPPATH2%\GameData\VladMultiplayer\Button")

xcopy /Y /S "%~dp0..\LmpClient\Localization\XML\*.*" "%KSPPATH%\GameData\VladMultiplayer\Localization"
IF DEFINED KSPPATH2 (xcopy /Y /S "%~dp0..\LmpClient\Localization\XML\*.*" "%KSPPATH2%\GameData\VladMultiplayer\Localization")

xcopy /Y /S "%~dp0..\LmpClient\ModuleStore\XML\*.xml" "%KSPPATH%\GameData\VladMultiplayer\PartSync"
IF DEFINED KSPPATH2 (xcopy /Y /S "%~dp0..\LmpClient\ModuleStore\XML\*.xml" "%KSPPATH2%\GameData\VladMultiplayer\PartSync")

xcopy /Y "%~dp0..\LmpClient\Resources\Icons\*.*" "%KSPPATH%\GameData\VladMultiplayer\Icons"
IF DEFINED KSPPATH2 (xcopy /Y "%~dp0..\LmpClient\Resources\Icons\*.*" "%KSPPATH2%\GameData\VladMultiplayer\Icons")

xcopy /Y "%~dp0..\LmpClient\Resources\Flags\*.*" "%KSPPATH%\GameData\VladMultiplayer\Flags"
IF DEFINED KSPPATH2 (xcopy /Y "%~dp0..\LmpClient\Resources\Flags\*.*" "%KSPPATH2%\GameData\VladMultiplayer\Flags")

xcopy /Y "%~dp0..\LmpClient\Resources\LoadingScreens\*.*" "%KSPPATH%\GameData\VladMultiplayer\LoadingScreens"
IF DEFINED KSPPATH2 (xcopy /Y "%~dp0..\LmpClient\Resources\LoadingScreens\*.*" "%KSPPATH2%\GameData\VladMultiplayer\LoadingScreens")

xcopy /Y "%~dp0..\LmpClient\Resources\LoadingScreens\VMPLoadingScreen.png" "%KSPPATH%\UserLoadingScreens"
IF DEFINED KSPPATH2 (xcopy /Y "%~dp0..\LmpClient\Resources\LoadingScreens\VMPLoadingScreen.png" "%KSPPATH2%\UserLoadingScreens")
