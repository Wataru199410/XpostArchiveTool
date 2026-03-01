@echo off
setlocal

set ROOT=%~dp0..
set ISCC="C:\Program Files (x86)\Inno Setup 6\ISCC.exe"

if not exist %ISCC% (
  echo Inno Setup 6 の ISCC.exe が見つかりません。
  echo 先に Inno Setup 6 をインストールしてください。
  exit /b 1
)

call "%~dp0publish.bat"
if errorlevel 1 exit /b 1

%ISCC% "%ROOT%\installer\XPostArchive.iss"
if errorlevel 1 (
  echo Installer build failed.
  exit /b 1
)

echo Installer build complete: %ROOT%\dist\installer
exit /b 0
