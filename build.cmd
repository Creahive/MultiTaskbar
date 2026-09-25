@echo off
rem Builds MultiTaskbar.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
rem No Visual Studio or .NET SDK needed.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo Could not find %CSC%
    exit /b 1
)
if not exist "%~dp0bin" mkdir "%~dp0bin"
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /out:"%~dp0bin\MultiTaskbar.exe" ^
    /win32icon:"%~dp0src\icon.ico" ^
    /r:System.Windows.Forms.dll /r:System.Drawing.dll "%~dp0src\MultiTaskbar.cs"
if errorlevel 1 exit /b 1
echo Built %~dp0bin\MultiTaskbar.exe
