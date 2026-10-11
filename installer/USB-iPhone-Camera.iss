#ifndef AppVersion
  #define AppVersion "0.2.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\windows"
#endif

[Setup]
AppId={{60EDC18E-1D07-4C96-8883-EE2C5AA3046B}
AppName=CamoRip
AppVersion={#AppVersion}
AppPublisher=Tskk-dev
DefaultDirName={autopf}\CamoRip
DefaultGroupName=CamoRip
UsePreviousGroup=no
UninstallDisplayIcon={app}\CamoRip.exe
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
PrivilegesRequired=admin
OutputDir=..\artifacts\installer
OutputBaseFilename=CamoRip-{#AppVersion}-windows-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
AppMutex=Local\UniversalCam_USB_SingleInstance
CloseApplications=yes
RestartApplications=no
LicenseFile=..\LICENSE

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "UCamMediaSource.dll"
Source: "{#PublishDir}\UCamMediaSource.dll"; DestDir: "{app}"; Flags: ignoreversion regserver

[Icons]
Name: "{group}\CamoRip"; Filename: "{app}\CamoRip.exe"
Name: "{group}\Uninstall CamoRip"; Filename: "{uninstallexe}"
Name: "{autodesktop}\CamoRip"; Filename: "{app}\CamoRip.exe"; Tasks: desktopicon

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked

[Run]
Filename: "{app}\CamoRip.exe"; Description: "Open CamoRip"; Flags: nowait postinstall skipifsilent runasoriginaluser

[InstallDelete]
; Keep the installer AppId and existing install path to upgrade in place.
Type: files; Name: "{app}\UniversalCam.exe"
Type: files; Name: "{autodesktop}\USB iPhone Camera.lnk"
Type: files; Name: "{autoprograms}\USB iPhone Camera\USB iPhone Camera.lnk"
Type: files; Name: "{autoprograms}\USB iPhone Camera\Uninstall USB iPhone Camera.lnk"
