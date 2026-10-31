@echo off
cd /d "%~dp0"
echo Building LiteIsland.exe, please wait...
echo (.NET 8 SDK and internet are needed ONLY on this PC, only for building.)
echo.
taskkill /f /im LiteIsland.exe >nul 2>nul
if exist release rmdir /s /q release
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o release
if errorlevel 1 (
    echo.
    echo Build failed. Send the error above to get it fixed.
    pause
    exit /b 1
)
echo.
echo Fetching built-in tools (yt-dlp, ffmpeg, Filee converter). First time takes a few minutes...
set FULLFLAG=
if /i "%~1"=="full" set FULLFLAG=-Full
powershell -NoProfile -ExecutionPolicy Bypass -File "tools\get-engines.ps1" -Release "%cd%\release" %FULLFLAG%
if errorlevel 1 (
    echo.
    echo Fetching the tools failed. Check your internet and run build.bat again.
    pause
    exit /b 1
)
for /f %%H in ('certutil -hashfile "release\LiteIsland.exe" SHA256 ^| findstr /r /x "[0-9a-fA-F][0-9a-fA-F]*"') do echo %%H  LiteIsland.exe > release\SHA256.txt
echo.
echo Done! Folder: %cd%\release
echo Copy the WHOLE release folder to any PC and run LiteIsland.exe. Nothing to download or install.
echo (build.bat full = also bundles LibreOffice, calibre and OCR for the rarest document formats, much bigger.)
echo.
pause
