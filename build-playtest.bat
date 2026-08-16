@echo off
setlocal

rem Same build against the 1.8 playtest, to check the mod still compiles and deploys on an
rem older build of the game. The Steam build is what build.bat targets.
rem
rem Two installs also make a two player test possible on one machine: host the playtest and
rem join with the Steam build, which is how the chain is actually checked.
pushd "%~dp0"

set GAME=D:\playtest\Flipping is Hard Playtest v1.8.001

if not exist "%GAME%\BepInEx\interop\Assembly-CSharp.dll" (
    echo No interop found in "%GAME%".
    echo Run the game once with BepInEx installed and try again.
    popd
    pause
    exit /b 1
)

dotnet build -c Release -p:GameDir="%GAME%" -p:Deploy=true
if errorlevel 1 (
    echo BUILD FAILED
    popd
    pause
    exit /b 1
)

popd

echo.
echo Done.
pause
