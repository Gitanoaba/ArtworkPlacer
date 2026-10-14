@echo off
setlocal
title Qbiq Tools - Uninstall

set "DEST=%APPDATA%\Autodesk\Revit\Addins\2024"

tasklist /FI "IMAGENAME eq Revit.exe" 2>nul | find /I "Revit.exe" >nul
if not errorlevel 1 (
  echo Revit is open. Close Revit and run Uninstall.bat again.
  echo.
  pause
  exit /b 1
)

for %%F in (ArtworkPlacer.dll ArtworkPlacer.addin ArtworkPlacer.png AlignHeights.png TvPlacer.png RoomStudio.png LogoPlacer.png) do (
  if exist "%DEST%\%%F" del /F /Q "%DEST%\%%F"
)

echo Qbiq Tools removed. The "Qbiq Tools" tab won't appear the next time you open Revit.
echo.
pause
