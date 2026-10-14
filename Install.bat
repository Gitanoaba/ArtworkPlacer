@echo off
setlocal
title Qbiq Tools - Install

rem Copia el add-in a la carpeta de Revit 2024 del usuario y lo desbloquea
set "DEST=%APPDATA%\Autodesk\Revit\Addins\2024"
set "SRC=%~dp0"

tasklist /FI "IMAGENAME eq Revit.exe" 2>nul | find /I "Revit.exe" >nul
if not errorlevel 1 (
  echo Revit is open. Close Revit and run Install.bat again.
  echo.
  pause
  exit /b 1
)

if not exist "%SRC%ArtworkPlacer.dll" (
  echo ArtworkPlacer.dll was not found next to Install.bat.
  echo If you opened the .zip directly, extract it first ^(right-click the zip ^> Extract All^).
  echo.
  pause
  exit /b 1
)

if not exist "%DEST%" mkdir "%DEST%"

for %%F in (ArtworkPlacer.dll ArtworkPlacer.addin ArtworkPlacer.png AlignHeights.png TvPlacer.png RoomStudio.png) do (
  if exist "%SRC%%%F" (
    copy /Y "%SRC%%%F" "%DEST%\" >nul || goto :failed
  )
)

rem Windows marca como bloqueados los archivos que vienen de internet o de un zip
powershell -NoProfile -ExecutionPolicy Bypass -Command "foreach ($f in 'ArtworkPlacer.dll','ArtworkPlacer.addin','ArtworkPlacer.png','AlignHeights.png','TvPlacer.png','RoomStudio.png') { $p = Join-Path $env:DEST $f; if (Test-Path -LiteralPath $p) { Unblock-File -LiteralPath $p } }" >nul 2>&1

echo Qbiq Tools installed.
echo.
echo Open Revit 2024: you'll find Room Studio, Place Artwork, Align Heights and Place TV
echo in the "Qbiq Tools" tab.
echo.
pause
exit /b 0

:failed
echo.
echo Could not copy the files to:
echo   %DEST%
echo Make sure Revit is closed and try again.
echo.
pause
exit /b 1
