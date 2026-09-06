@echo off
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if not exist "%CSC%" (
    echo.
    echo [ERROR] Windows built-in .NET Framework compiler was not found.
    echo Please send this screen back to ChatGPT.
    echo.
    pause
    exit /b 1
)

echo Building MXBackspaceHold.exe...
"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu ^
 /r:System.dll ^
 /r:System.Core.dll ^
 /r:System.Drawing.dll ^
 /r:System.Windows.Forms.dll ^
 /r:"%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\UIAutomationTypes\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationTypes.dll" ^
 /r:"%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\UIAutomationClient\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationClient.dll" ^
 /out:"%~dp0MXBackspaceHold.exe" "%~dp0Program.cs"

if errorlevel 1 (
    echo.
    echo [ERROR] Build failed. Please send this window back to ChatGPT.
    echo.
    pause
    exit /b 1
)

echo.
echo Build succeeded.
echo Starting MXBackspaceHold v1.4...
start "" "%~dp0MXBackspaceHold.exe"
echo.
echo Done. Look for MXBackspaceHold v1.4 in the system tray.
echo You can close this window.
pause
