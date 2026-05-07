; Inno Setup Script for RawTorrent
#define AppName "RawTorrent"
#define AppVersion "1.0.0"
#define AppPublisher "RawTorrent Team"
#define AppExeName "TorServices.exe"
#define AppId "{{B5A2D1B8-5C9B-4D1E-8B7C-4C9D9E2F3A1B}"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
AllowNoIcons=yes
OutputDir=..\installer
OutputBaseFilename=RawTorrentSetup
Compression=lzma
SolidCompression=yes
WizardStyle=modern
SetupIconFile=Resources\app_icon.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: ".\publish\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon; IconFilename: "{app}\{#AppExeName}"

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
// Removed the .NET 10 check because the application is published as 
// "Self-Contained", meaning it already includes the .NET 10 runtime 
// inside the EXE. No system-wide installation is required.
