#ifndef AppVersion
  #define AppVersion "0.4.0"
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
AppMutex=Local\CamoRip_USB_SingleInstance
CloseApplications=yes
RestartApplications=no
LicenseFile=..\LICENSE

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "CamoRipMediaSource.dll,startup-check-data\*"
Source: "{#PublishDir}\CamoRipMediaSource.dll"; DestDir: "{app}"; Flags: ignoreversion regserver

[Icons]
Name: "{group}\CamoRip"; Filename: "{app}\CamoRip.exe"
Name: "{group}\Uninstall CamoRip"; Filename: "{uninstallexe}"
Name: "{autodesktop}\CamoRip"; Filename: "{app}\CamoRip.exe"; Tasks: desktopicon

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked

[Run]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""CamoRip Local Video"""; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""CamoRip Local Discovery"""; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""CamoRip Local Video"" dir=in action=allow program=""{app}\CamoRip.exe"" protocol=TCP localport=7781 profile=private remoteip=localsubnet"; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""CamoRip Local Discovery"" dir=in action=allow program=""{app}\CamoRip.exe"" protocol=UDP localport=5353 profile=private remoteip=localsubnet"; Flags: runhidden
Filename: "{app}\CamoRip.exe"; Description: "Open CamoRip"; Flags: nowait postinstall skipifsilent runasoriginaluser

[InstallDelete]
; Keep the installer AppId and existing install path to upgrade in place.
Type: files; Name: "{autodesktop}\USB iPhone Camera.lnk"
Type: files; Name: "{autoprograms}\USB iPhone Camera\USB iPhone Camera.lnk"
Type: files; Name: "{autoprograms}\USB iPhone Camera\Uninstall USB iPhone Camera.lnk"

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""CamoRip Local Video"""; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""CamoRip Local Discovery"""; Flags: runhidden
