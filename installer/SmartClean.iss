; Single distributable EXE containing the entire proven multi-file WinUI 3 publish output.
#ifndef AppVersion
  #define AppVersion "0.4.0.0"
#endif
#ifndef BuildOutput
  #define BuildOutput "..\output\SmartClean-win-x64"
#endif
[Setup]
AppId={{57EAFAB5-EA6B-48F7-BC6B-7692D71B2CAA}
AppName=SupaClean
AppVersion={#AppVersion}
AppPublisher=SupaClean
DefaultDirName={localappdata}\Programs\SmartClean
DefaultGroupName=SupaClean
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2
SolidCompression=yes
OutputDir=..\output\releases
OutputBaseFilename=SmartClean-Setup
UninstallDisplayIcon={app}\SmartClean.WinUI.exe
CloseApplications=yes
RestartApplications=no
DisableWelcomePage=yes
[Files]
Source: "{#BuildOutput}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Keep the original AppId and install location so old SmartClean installations
; upgrade in place without losing LOCALAPPDATA/SmartClean/Recovery or settings.
; The single release asset remains SmartClean-Setup.exe for backward-compatible
; update checks in the previously installed client. Only visible branding changes.
[InstallDelete]
Type: files; Name: "{autoprograms}\SmartClean.lnk"
Type: files; Name: "{autodesktop}\SmartClean.lnk"
[Icons]
Name: "{autoprograms}\SupaClean"; Filename: "{app}\SmartClean.WinUI.exe"
Name: "{autodesktop}\SupaClean"; Filename: "{app}\SmartClean.WinUI.exe"; Tasks: desktopicon
[Tasks]
Name: "desktopicon"; Description: "Create desktop shortcut"; Flags: unchecked
[Run]
Filename: "{app}\SmartClean.WinUI.exe"; Description: "Open SupaClean"; Flags: nowait postinstall skipifsilent
