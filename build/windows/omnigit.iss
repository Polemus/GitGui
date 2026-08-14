; Inno Setup script for Omnigit.
; Compiled by build/windows/package.ps1 - which passes AppVersion, SourceDir,
; OutputDir and RID in on the command line, so this file needs no editing.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\build\.stage-win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\dist"
#endif
#ifndef Rid
  #define Rid "win-x64"
#endif

[Setup]
AppId={{9F2C6B31-7A4E-4D18-9C55-2E7B1A4F8D30}
AppName=Omnigit
AppVersion={#AppVersion}
AppPublisher=Polemus
AppPublisherURL=https://github.com/Polemus/Omnigit
DefaultDirName={autopf}\Omnigit
DefaultGroupName=Omnigit
UninstallDisplayIcon={app}\Omnigit.exe
OutputDir={#OutputDir}
OutputBaseFilename=Omnigit-{#AppVersion}-{#Rid}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Per-user install needs no elevation; switches to admin if the user picks
; a machine-wide location.
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Omnigit"; Filename: "{app}\Omnigit.exe"
Name: "{group}\Uninstall Omnigit"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Omnigit"; Filename: "{app}\Omnigit.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Omnigit.exe"; Description: "Launch Omnigit"; Flags: nowait postinstall skipifsilent
