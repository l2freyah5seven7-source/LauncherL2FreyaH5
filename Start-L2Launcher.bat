@echo off
setlocal
if not exist "%~dp0LineageII.exe" (
    echo No se encuentra el launcher: "%~dp0LineageII.exe"
    pause
    exit /b 1
)
start "" "%~dp0LineageII.exe"
endlocal
