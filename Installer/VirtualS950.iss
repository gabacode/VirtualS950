; VirtualS950 - installer for the editor and the plugin.
;
; Inno Setup 6.3 or newer. build-installer.ps1 builds both products, checks every file
; named below actually exists, and then runs this.
;
; WHY BOTH IN ONE INSTALLER
;
; They are two halves of the same thing. The editor opens disk images, edits programmes and
; writes them back to something a real S950 will load; the plugin plays those same disks in
; a DAW. Both are driven by the same measured engine - the numbers in Cal.cs and Cal.h agree
; to the digit - so shipping them together is shipping one instrument with two front ends.
;
; WHY IT ASKS WHO IT IS FOR
;
; A VST3 has two homes on Windows: the machine-wide one under Common Files, which needs
; administrator rights, and the per-user one under LocalAppData, which does not. Inno's
; {autocf} resolves to whichever matches the choice made at the start, so the same script
; does both and neither needs explaining.

#define AppName        "VirtualS950"
#define AppVersion     "0.1.0"
#define AppPublisher   "simozzer"
#define AppURL         "https://github.com/simozzer/VirtualS950"

#define RepoRoot       ".."
#define PluginArtefacts RepoRoot + "\Plugin\build\VirtualS950_artefacts\Release"

[Setup]
AppId={{7A1D5C4E-9B62-4E8B-9F3A-0C51A4D9E7B2}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
OutputDir=Output
OutputBaseFilename=VirtualS950-{#AppVersion}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

; The plugin is 64-bit only, and so is the editor's build.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Ask rather than assume. A per-user install needs no administrator and puts the VST3
; where a host will still find it; a machine-wide one is what a studio machine expects.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\AkaiS950Studio.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Types]
Name: "full";   Description: "Everything"
Name: "custom"; Description: "Choose what to install"; Flags: iscustom

[Components]
Name: "studio";     Description: "Akai S950 Studio - open, edit and save disk images"; Types: full custom; Flags: checkablealone
Name: "vst3";       Description: "VST3 plugin - play disks in a DAW";                  Types: full custom; Flags: checkablealone
Name: "standalone"; Description: "Standalone player - the plugin without a DAW";       Types: full custom; Flags: checkablealone

[Files]
Source: "{#RepoRoot}\AkaiS950Studio.exe"; DestDir: "{app}"; Components: studio; Flags: ignoreversion

Source: "{#PluginArtefacts}\Standalone\VirtualS950.exe"; DestDir: "{app}"; Components: standalone; Flags: ignoreversion

; A VST3 is a folder, not a file - the binary lives at Contents\x86_64-win inside it - so
; the whole bundle is copied and the structure kept.
Source: "{#PluginArtefacts}\VST3\VirtualS950.vst3\*"; DestDir: "{autocf}\VST3\VirtualS950.vst3"; \
    Components: vst3; Flags: ignoreversion recursesubdirs createallsubdirs

Source: "{#RepoRoot}\README.md";        DestDir: "{app}"; Flags: ignoreversion isreadme
Source: "{#RepoRoot}\Plugin\README.md"; DestDir: "{app}"; DestName: "README-plugin.md"; Flags: ignoreversion

[Icons]
Name: "{group}\Akai S950 Studio";        Filename: "{app}\AkaiS950Studio.exe"; Components: studio
Name: "{group}\VirtualS950 Standalone";  Filename: "{app}\VirtualS950.exe";    Components: standalone
Name: "{group}\Uninstall {#AppName}";    Filename: "{uninstallexe}"

Name: "{autodesktop}\Akai S950 Studio";  Filename: "{app}\AkaiS950Studio.exe"; Components: studio; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut for the editor"; \
    GroupDescription: "Shortcuts:"; Components: studio; Flags: unchecked

[Run]
Filename: "{app}\AkaiS950Studio.exe"; Description: "Open Akai S950 Studio"; \
    Components: studio; Flags: nowait postinstall skipifsilent

[Code]
//
// The editor is a .NET Framework 4 program. Windows 10 from 1903 and every Windows 11 ship
// 4.8 in the box, so this will almost never fire - but "nothing happens when I run it" is a
// miserable way to discover otherwise, and the check costs one registry read.
//
function HasDotNetFramework4: Boolean;
var
  Release: Cardinal;
begin
  Result := RegQueryDWordValue (HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full',
                                'Release', Release);
end;

function InitializeSetup: Boolean;
begin
  Result := True;

  if not HasDotNetFramework4 then
    Result := MsgBox ('The editor needs the .NET Framework 4, which does not appear to be '
                      + 'installed.' + #13#10#13#10
                      + 'The plugin does not need it, so installing only that will work. '
                      + 'Carry on?',
                      mbConfirmation, MB_YESNO) = IDYES;
end;

//
// Say where the VST3 went. Hosts differ on which folders they scan, and the per-user one
// is the one they are most likely to have to be told about once.
//
procedure CurStepChanged (CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsComponentSelected ('vst3') and not IsAdminInstallMode then
    MsgBox ('The plugin was installed for you only, at:' + #13#10#13#10
            + ExpandConstant ('{autocf}\VST3') + #13#10#13#10
            + 'If your DAW does not find it, add that folder to its VST3 search paths. '
            + 'In Ableton: Preferences, Plug-Ins, VST3 Plug-In Custom Folder.',
            mbInformation, MB_OK);
end;
