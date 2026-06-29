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
#define AppPublisher "OpenLogi"
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
