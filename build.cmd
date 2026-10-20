@echo off
setlocal
pushd "%~dp0" || exit /b 1
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo csc.exe not found
    popd
    exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\restore-deps.ps1"
if errorlevel 1 (
    popd
    exit /b 1
)
"%CSC%" /nologo /target:exe /out:patch.exe /r:dnlib.dll src\patch.cs
if errorlevel 1 (
    popd
    exit /b 1
)
echo Build OK: patch.exe
popd
exit /b 0
