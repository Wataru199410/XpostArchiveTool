#define MyAppName "X Post Archive"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "X Post Archive Project"
#define MyAppExeName "XPostArchive.Desktop.exe"

[Setup]
AppId={{B2E35395-9CC8-4BA1-9B61-252E5E9BC4C9}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\XPostArchive
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\dist\installer
OutputBaseFilename=XPostArchive-Setup
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Tasks]
Name: "desktopicon"; Description: "デスクトップにショートカットを作成する"; GroupDescription: "追加タスク:"; Flags: unchecked

[Files]
Source: "..\dist\publish\desktop\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
Source: "..\dist\publish\server\*"; DestDir: "{app}\server"; Flags: recursesubdirs ignoreversion
Source: "..\extension\*"; DestDir: "{app}\extension"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{#MyAppName} を起動する"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    MsgBox('初回のみ Chrome 拡張の読み込みが必要です。'#13#10 +
      'Chrome -> chrome://extensions -> デベロッパーモードON -> "' +
      ExpandConstant('{app}\extension') + '" を読み込んでください。',
      mbInformation, MB_OK);
  end;
end;
