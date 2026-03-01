@echo off
setlocal

set ROOT=%~dp0..
set OUT=%ROOT%\dist\publish
set FFMPEG_SRC=%ROOT%\third_party\ffmpeg\ffmpeg.exe
set FFMPEG_DST_DIR=%OUT%\server\tools

if exist "%OUT%" rmdir /s /q "%OUT%"
mkdir "%OUT%\desktop"
mkdir "%OUT%\server"

if not exist "%FFMPEG_SRC%" (
  echo ffmpeg.exe が見つかりません: %FFMPEG_SRC%
  echo third_party\ffmpeg\ffmpeg.exe を配置してください。
  exit /b 1
)

echo [1/2] Desktop publish...
dotnet publish "%ROOT%\desktop\XPostArchive.Desktop.csproj" -c Release -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true -o "%OUT%\desktop"
if errorlevel 1 goto :error

echo [2/2] Server publish...
dotnet publish "%ROOT%\server\XPostArchive.Api.csproj" -c Release -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true -o "%OUT%\server"
if errorlevel 1 goto :error

mkdir "%FFMPEG_DST_DIR%"
copy /y "%FFMPEG_SRC%" "%FFMPEG_DST_DIR%\ffmpeg.exe" >nul
if errorlevel 1 goto :error

echo Publish complete.
exit /b 0

:error
echo Publish failed.
exit /b 1
