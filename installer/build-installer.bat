@echo off
cd /d "%~dp0\.."
echo Publishing OverByte (Release, win-x64, self-contained, single file)...
dotnet publish OverByte.csproj -c Release -r win-x64 --self-contained true
if %ERRORLEVEL% NEQ 0 (echo BUILD FAILED & pause & exit /b 1)

set ISCC="C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if not exist %ISCC% set ISCC="C:\Program Files\Inno Setup 6\ISCC.exe"
if not exist %ISCC% (
    echo Inno Setup 6 not found. Install it from https://jrsoftware.org/isdl.php
    pause
    exit /b 1
)

echo Compiling installer...
%ISCC% "%~dp0OverByte.iss"
if %ERRORLEVEL% NEQ 0 (echo INSTALLER BUILD FAILED & pause & exit /b 1)

echo.
echo SUCCESS: installer\Output\OverByte-Setup-1.0.0.exe
pause
