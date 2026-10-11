#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\windows"
#endif

[Setup]
AppId={{60EDC18E-1D07-4C96-8883-EE2C5AA3046B}
AppName=USB iPhone Camera
AppVersion={#AppVersion}
AppPublisher=Tskk-dev
DefaultDirName={autopf}\USB iPhone Camera
DefaultGroupName=USB iPhone Camera
UninstallDisplayIcon={app}\UniversalCam.exe
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
PrivilegesRequired=admin
OutputDir=..\artifacts\installer
OutputBaseFilename=USB-iPhone-Camera-{#AppVersion}-windows-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
LicenseFile=..\LICENSE

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "UCamMediaSource.dll"
Source: "{#PublishDir}\UCamMediaSource.dll"; DestDir: "{app}"; Flags: ignoreversion regserver

[Icons]
Name: "{group}\USB iPhone Camera"; Filename: "{app}\UniversalCam.exe"
Name: "{group}\Uninstall USB iPhone Camera"; Filename: "{uninstallexe}"
Name: "{autodesktop}\USB iPhone Camera"; Filename: "{app}\UniversalCam.exe"; Tasks: desktopicon

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked

[Run]
Filename: "{app}\UniversalCam.exe"; Description: "Open USB iPhone Camera"; Flags: nowait postinstall skipifsilent runasoriginaluser
