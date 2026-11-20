#ifndef AppVersion
#define AppVersion "0.4.0"
#endif

[Setup]
AppId={{26C88CB9-4606-45FC-B56D-942D25BA4727}
AppName=Device Lab
AppVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\Device Lab
DefaultGroupName=Device Lab
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..\artifacts
OutputBaseFilename=DeviceLab-setup-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\DeviceLab.exe

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\Device Lab"; Filename: "{app}\DeviceLab.exe"

[Run]
Filename: "{app}\DeviceLab.exe"; Description: "Open Device Lab"; Flags: nowait postinstall skipifsilent
