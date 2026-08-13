; Inno Setup script for GitGui.
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
AppName=GitGui
AppVersion={#AppVersion}
AppPublisher=Polemus
AppPublisherURL=https://github.com/Polemus/GitGui
DefaultDirName={autopf}\GitGui
DefaultGroupName=GitGui
UninstallDisplayIcon={app}\GitGui.exe
OutputDir={#OutputDir}
OutputBaseFilename=GitGui-{#AppVersion}-{#Rid}-setup
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
Name: "{group}\GitGui"; Filename: "{app}\GitGui.exe"
Name: "{group}\Uninstall GitGui"; Filename: "{uninstallexe}"
Name: "{autodesktop}\GitGui"; Filename: "{app}\GitGui.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\GitGui.exe"; Description: "Launch GitGui"; Flags: nowait postinstall skipifsilent
