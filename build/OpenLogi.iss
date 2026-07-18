; Inno Setup script for OpenLogi.net.
; Compiled by the "Release" GitHub Action with ISCC. The version and the
; published-output directory are injected from the workflow via /D defines:
;   ISCC /DAppVersion=1.2.3 /DSourceDir=C:\...\publish\win-x64 build\OpenLogi.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\win-x64"
#endif

#define AppName "OpenLogi.net"
#define AppExe "OpenLogi.App.exe"
#define AppPublisher "LoxSmoke"
#define AppUrl "https://github.com/LoxSmoke/openlogi-net"

[Setup]
; A stable AppId keeps upgrades/uninstall pointing at the same install.
AppId={{8E0F7A12-BFB3-4FE8-B9A5-48FD50A15A9A}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={autopf}\OpenLogi.net
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputDir=installer
OutputBaseFilename=OpenLogi.net-{#AppVersion}-setup
SetupIconFile=..\src\OpenLogi.App\Assets\icon.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; net10.0-windows is 64-bit; install into the native Program Files.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunRegKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunRegValue = 'OpenLogi'; // written at runtime by Autostart.cs

// True when the uninstaller was invoked with the given switch, e.g.
//   unins000.exe /VERYSILENT /PURGEDATA
// (winget can't forward custom uninstall switches, so scripted purges call
// the uninstaller directly).
function CmdLineParamExists(const Value: string): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Value) = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Cmd: string;
  PurgeData: Boolean;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // Remove the launch-at-login entry the app writes at runtime — without this,
    // Windows keeps trying to start the deleted exe at every logon. Only removed
    // when it points into this install, so an entry from a copy elsewhere (e.g.
    // a dev build) is left alone. NB: a machine-scope uninstall runs elevated,
    // so HKCU is the uninstalling user's hive; other accounts keep their entry.
    if RegQueryStringValue(HKEY_CURRENT_USER, RunRegKey, RunRegValue, Cmd)
       and (Pos(Lowercase(ExpandConstant('{app}')), Lowercase(Cmd)) > 0) then
      RegDeleteValue(HKEY_CURRENT_USER, RunRegKey, RunRegValue);

    // Remove per-user data (config, cached device images, logs) when asked:
    // either the explicit /PURGEDATA switch, or a Yes on the interactive prompt.
    // Plain silent uninstalls (winget/scripted) keep data, both to avoid
    // blocking on a prompt and so upgrades can never wipe settings.
    PurgeData := CmdLineParamExists('/PURGEDATA');
    if not PurgeData and not UninstallSilent then
      PurgeData := MsgBox('Also remove your OpenLogi.net settings, cached device images and logs?'
                + #13#10#13#10 + 'Choose No to keep them for a future reinstall.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
    if PurgeData then
    begin
      DelTree(ExpandConstant('{userappdata}\OpenLogi'), True, True, True);
      DelTree(ExpandConstant('{localappdata}\OpenLogi'), True, True, True);
    end;
  end;
end;
