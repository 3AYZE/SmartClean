; Single distributable EXE containing the entire proven multi-file WinUI 3 publish output.
#ifndef AppVersion
  #define AppVersion "0.2.0.0"
#endif
#ifndef BuildOutput
  #define BuildOutput "..\output\SmartClean-win-x64"
#endif
[Setup]
AppId={{57EAFAB5-EA6B-48F7-BC6B-7692D71B2CAA}
AppName=SmartClean
AppVersion={#AppVersion}
AppPublisher=SmartClean
DefaultDirName={localappdata}\Programs\SmartClean
DefaultGroupName=SmartClean
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
[Icons]
Name: "{autoprograms}\SmartClean"; Filename: "{app}\SmartClean.WinUI.exe"
Name: "{autodesktop}\SmartClean"; Filename: "{app}\SmartClean.WinUI.exe"; Tasks: desktopicon
[Tasks]
Name: "desktopicon"; Description: "Create desktop shortcut"; Flags: unchecked
[Run]
Filename: "{app}\SmartClean.WinUI.exe"; Description: "Open SmartClean"; Flags: nowait postinstall skipifsilent
