; Inno Setup script. Built by .github/workflows/release.yml:
;   ISCC /DAppVersion=1.7.0 /DSourceDir=<release folder> installer\LiteIsland.iss
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\release"
#endif

[Setup]
AppId={{B5A3D3F2-7C1E-4E58-9A56-4D2F6C8E1A90}
AppName=LiteIsland
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
AppPublisher=Hussain
; installs for the current user only: no admin rights, and the app can update itself in this folder
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\LiteIsland
DisableProgramGroupPage=yes
DisableDirPage=auto
OutputDir=out
OutputBaseFilename=LiteIsland-Setup-{#AppVersion}
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\LiteIsland.exe
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=zip/1
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{userprograms}\LiteIsland"; Filename: "{app}\LiteIsland.exe"
Name: "{userdesktop}\LiteIsland"; Filename: "{app}\LiteIsland.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\LiteIsland.exe"; Description: "Start LiteIsland"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
Type: filesandordirs; Name: "{localappdata}\LiteIsland\bundle"

[Code]
procedure StopApp();
var R: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im LiteIsland.exe', '', SW_HIDE, ewWaitUntilTerminated, R);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopApp();
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then StopApp();
end;
