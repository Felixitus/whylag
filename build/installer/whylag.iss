; Inno Setup script for WhyLag. Build with build/build-installer.ps1 after build/publish.ps1.
; Per-user installation: no administrator rights, nothing outside the user's profile.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define AppName "WhyLag"
#define AppExe "WhyLag.exe"
#define SourceDir "..\..\artifacts\WhyLag"

[Setup]
AppId={{7D3B6C1E-5F2A-4C8B-9E41-57A1F0C2D9B3}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=WhyLag contributors
AppPublisherURL=https://github.com/Felixitus/whylag
AppSupportURL=https://github.com/Felixitus/whylag/issues
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19045
OutputDir=..\..\artifacts
OutputBaseFilename=WhyLag-{#AppVersion}-setup
SetupIconFile=..\..\assets\whylag.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
LicenseFile=..\..\LICENSE
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
VersionInfoVersion={#AppVersion}

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"

[CustomMessages]
en.AutoStart=Start WhyLag when I sign in to Windows (recommended – it can only explain lags it has recorded)
de.AutoStart=WhyLag bei der Windows-Anmeldung starten (empfohlen – es kann nur aufgezeichnete Ruckler erklären)
en.DeleteData=Also delete WhyLag's saved incidents and settings?%n%n%1
de.DeleteData=Auch die gespeicherten Vorfälle und Einstellungen von WhyLag löschen?%n%n%1
en.Launch=Start WhyLag now
de.Launch=WhyLag jetzt starten

[Tasks]
Name: "autostart"; Description: "{cm:AutoStart}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\WhyLag.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\whylag-cli.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\tools\*"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "{#SourceDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion isreadme
Source: "{#SourceDir}\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Parameters: "--show"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Parameters: "--show"; Tasks: desktopicon

[Registry]
; Same value WhyLag's own "Start with Windows" setting writes, so both stay in sync.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "WhyLag"; \
  ValueData: """{app}\{#AppExe}"""; Tasks: autostart; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "WhyLag"; Flags: uninsdeletevalue dontcreatekey

[Run]
Filename: "{app}\{#AppExe}"; Parameters: "--show"; Description: "{cm:Launch}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Stops the frame-time trace session in case WhyLag did not exit cleanly.
Filename: "{app}\tools\PresentMon-2.6.0-x64.exe"; Parameters: "--terminate_existing_session --session_name WhyLag"; \
  Flags: runhidden skipifdoesntexist; RunOnceId: "StopTraceSession"

[Code]
{ Asks a running WhyLag to exit cleanly (it then also stops PresentMon and its trace session). }
procedure StopRunningWhyLag(const Exe: String);
var
  ResultCode: Integer;
begin
  if FileExists(Exe) then
  begin
    Exec(Exe, '--exit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(2500);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningWhyLag(ExpandConstant('{app}\{#AppExe}'));
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  StopRunningWhyLag(ExpandConstant('{app}\{#AppExe}'));
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\WhyLag');
    if DirExists(DataDir) and not UninstallSilent then
      if MsgBox(FmtMessage(CustomMessage('DeleteData'), [DataDir]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
