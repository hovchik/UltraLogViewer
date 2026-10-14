; Inno Setup script for UltraLogViewer (Windows x64).
;
; The script packages whatever app folder it is given. By default that is the
; self-contained Avalonia/.NET publish output; the Electron build works too:
;
;   Avalonia:  iscc /DAppVersion=0.2.0 UltraLogViewer.iss
;   Electron:  iscc /DAppVersion=0.1.0 /DAppSourceDir=..\release\win-unpacked /DAppFlavor=electron UltraLogViewer.iss
;
; scripts/build-installer.ps1 builds the app and runs this for you.
; Paths are relative to this file.

#ifndef AppVersion
  #define AppVersion "0.2.0"
#endif
#ifndef AppSourceDir
  #define AppSourceDir "..\release\avalonia-win-x64"
#endif
#ifndef AppExeName
  #define AppExeName "UltraLogViewer.exe"
#endif
#ifndef AppFlavor
  #define AppFlavor "avalonia"
#endif
#ifndef OutputDir
  #define OutputDir "..\release\installer"
#endif

#define AppName "UltraLogViewer"
#define AppPublisher "UltraLogViewer"
#define AppUrl "https://github.com/hovchik/UltraLogViewer"
#define ProgId "UltraLogViewer.LogFile"

#if !FileExists(AddBackslash(AppSourceDir) + AppExeName)
  #error "App not found: run the app build first (see scripts/build-installer.ps1), or pass /DAppSourceDir=<folder containing the exe>."
#endif

#if AppFlavor == "avalonia"
  #define OutputSuffix ""
#else
  #define OutputSuffix "-" + AppFlavor
#endif

[Setup]
; Keep AppId stable forever: it is how upgrades and the uninstaller find an existing install.
AppId={{A78CE596-E881-44AF-88E4-F0AFFFF7CE4E}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Installs per user without admin by default; the user can choose "all users" on the first page.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
LicenseFile=..\LICENSE
UninstallDisplayIcon={app}\{#AppExeName}
SetupIconFile=..\dotnet\src\UltraLogViewer.Desktop\Assets\app.ico
UninstallDisplayName={#AppName}
ChangesAssociations=yes
CloseApplications=yes
RestartApplications=no
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
OutputDir={#OutputDir}
OutputBaseFilename={#AppName}-Setup-{#AppVersion}-x64{#OutputSuffix}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "contextmenu"; Description: "Add ""Open with {#AppName}"" to the right-click menu for all files"; GroupDescription: "Shell integration:"
Name: "assoclog"; Description: "Make {#AppName} the default app for .log files"; GroupDescription: "Shell integration:"; Flags: unchecked

[Files]
Source: "{#AppSourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; ProgId used by "Open with" and the optional .log association.
Root: HKA; Subkey: "Software\Classes\{#ProgId}"; ValueType: string; ValueName: ""; ValueData: "Log file"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\{#ProgId}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"
Root: HKA; Subkey: "Software\Classes\{#ProgId}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""
; Always offer the app under "Open with" for common log extensions, without taking over the default.
Root: HKA; Subkey: "Software\Classes\.log\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.txt\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\.gz\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\SupportedTypes"; ValueType: string; ValueName: ".log"; ValueData: ""; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExeName}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""
; Optional: default handler for .log.
Root: HKA; Subkey: "Software\Classes\.log"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: assoclog
; Optional: right-click "Open with UltraLogViewer" on any file.
Root: HKA; Subkey: "Software\Classes\*\shell\{#AppName}"; ValueType: string; ValueName: ""; ValueData: "Open with {#AppName}"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\*\shell\{#AppName}"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#AppExeName},0"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\*\shell\{#AppName}\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""; Tasks: contextmenu

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
