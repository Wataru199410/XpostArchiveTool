@echo off
setlocal

set "ROOT=%~dp0"
set "ISCC_X86=C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
set "ISCC_USER=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
set "ISCC="

if exist "%ISCC_X86%" set "ISCC=%ISCC_X86%"
if not defined ISCC if exist "%ISCC_USER%" set "ISCC=%ISCC_USER%"

if not defined ISCC (
  echo Inno Setup 6 ISCC.exe not found.
  echo Please install Inno Setup 6 first.
  exit /b 1
)

call "%ROOT%publish.bat"
if errorlevel 1 exit /b 1

"%ISCC%" "%ROOT%XPostArchive.iss"
if errorlevel 1 (
  echo Installer build failed.
  exit /b 1
)

echo Installer build complete: %ROOT%dist\installer
exit /b 0
