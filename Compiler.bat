@echo off
chcp 65001 >nul
cd /d "%~dp0"
rem Compile Eclipse.exe avec le compilateur du .NET Framework 4, deja present dans Windows. Rien a installer.
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo Compilateur introuvable : .NET Framework 4 n'est pas present.
  pause
  exit /b 1
)
"%CSC%" /nologo /codepage:65001 /target:winexe /optimize+ /win32icon:Eclipse.ico /out:Eclipse.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:System.Management.dll Eclipse.cs
if errorlevel 1 (
  echo.
  echo La compilation a echoue : le message ci-dessus dit pourquoi.
  pause
  exit /b 1
)
echo.
echo C'est bon : Eclipse.exe est pret, lancez-le d'un double-clic.
pause
