; Inno Setup script for Teeline. Built and signed by
; .github/workflows/release.yml - PublishDir and TEELINE_VERSION are set by
; that workflow; run locally with:
;   iscc /DPublishDir=..\MeetingTranscriber\bin\Release\net8.0-windows\win-x64\publish /DAppVersion=0.1.0 Teeline.iss
#ifndef PublishDir
  #define PublishDir "..\MeetingTranscriber\bin\Release\net8.0-windows\win-x64\publish"
#endif
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#define MyAppName "Teeline"
#define MyAppPublisher "FBL Consulting Ltd"
#define MyAppExeName "Teeline.exe"

[Setup]
; Fixed id so upgrades replace the previous install rather than side-by-side.
AppId={{A6E2B6B0-6C1E-4C7B-9A9F-9B9C6D6A6E10}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppPublisher={#MyAppPublisher}
; Per-user install, no admin/UAC prompt: {autopf}/{group}/{autodesktop} below
; all resolve to per-user locations (e.g. %LocalAppData%\Programs) once
; PrivilegesRequired is "lowest" rather than the Inno Setup default of
; "admin". PrivilegesRequiredOverridesAllowed still lets someone explicitly
; ask for an admin/machine-wide install (e.g. `TeelineSetup.exe /ALLUSERS`)
; if they want one, but that is never the default.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline dialog
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputBaseFilename=TeelineSetup
OutputDir=Output
Compression=lzma
SolidCompression=yes
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
SetupIconFile=..\MeetingTranscriber\assets\teeline.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
DisableProgramGroupPage=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop icon"; GroupDescription: "Additional icons:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
