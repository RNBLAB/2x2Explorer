; 2x2 Explorer - Inno Setup script
;
; Built by pack.cmd in the repository root. pack.cmd reads the version from
; Directory.Build.props and passes it in as AppVersion, so the version lives
; in one place only (see docs/releasing.md).
;
; Compile by hand:
;   ISCC.exe /DAppVersion=1.0.0 installer\2x2Explorer.iss
;
; ASCII only in this file, like the .cmd files: the console code page on the
; build machine is not 949 and non-ASCII here is easy to get wrong.

#define AppName    "2x2 Explorer"
#define AppExeName "2x2Explorer.exe"
#define AppPublisher "RNBLAB"
#define AppUrl     "https://github.com/RNBLAB/2x2Explorer"

; pack.cmd passes this in. The fallback only exists so the script still
; compiles when someone runs ISCC by hand without /D.
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

; Where dotnet publish put the self-contained build.
#define PublishDir "..\build\publish\win-x64"

; AppId, written once. The uninstall registry key uses it as is. [Setup]
; needs one more opening brace in front, because Inno Setup reads a single
; brace there as the start of a constant. Built by string joining on purpose:
; two opening braces written right before a preprocessor call would be read by
; the preprocessor as an escape and the call would never be expanded.
#define AppGuid "{59B1F1A3-F6AC-441C-901A-A257A62E4F6A}"
#define AppIdSetup "{" + AppGuid

; Named mutexes the app holds while it runs (src\Explorer.App\Services\
; RunningMarker.cs). Change both places together.
#define AppMutexNames "2x2Explorer.Running,Global\2x2Explorer.Running"

[Setup]
; Never change AppId. Windows uses it to recognise an existing install and
; upgrade it in place instead of leaving two copies behind.
AppId={#AppIdSetup}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}

; Installs into the user's own folder, so no administrator prompt. A user who
; wants a machine-wide install can still pick that in the first dialog.
DefaultDirName={autopf}\2x2Explorer
DefaultGroupName={#AppName}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

OutputDir=..\build\dist
OutputBaseFilename=2x2Explorer-{#AppVersion}-setup
SetupIconFile=..\src\Explorer.App\Resources\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
LicenseFile=..\LICENSE

Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes

; Offers to close a running copy instead of failing on a locked file.
CloseApplications=yes
RestartApplications=no

; Setup and the uninstaller both check these at start and ask the user to
; close 2x2 Explorer first. 1.0.0 did not hold the mutex yet, so a running
; 1.0.0 is only caught by CloseApplications above.
AppMutex={#AppMutexNames}

[Languages]
; pack.cmd puts Korean.isl next to this script when it can find one, so the
; Korean wizard appears without anyone editing this file. Listed first, which
; makes it the fallback when Windows is set to a language we do not carry.
; If the file is not there the installer is built with English only.
; Our own Korean strings for the [Code] section live in Korean.custom.isl
; (UTF-8) so that this file stays ASCII. The English ones are further down.
#if FileExists(AddBackslash(SourcePath) + "Korean.isl")
Name: "korean"; MessagesFile: "Korean.isl,Korean.custom.isl"
#endif
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
english.OldCopyRunning=2x2 Explorer is still running. Close every 2x2 Explorer window and run setup again.
english.OldRemoveFailed=The previous version could not be removed (code %1). Close 2x2 Explorer and run setup again.%n%nYour settings are kept either way.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The whole self-contained publish folder. Debug symbols are left out.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

; Settings live in %APPDATA%\2x2Explorer and are deliberately left behind on
; uninstall (there is no [UninstallDelete] section), so reinstalling keeps the
; panes, favourites and the rename log.

[Code]
{ ---------------------------------------------------------------------------
  Upgrade: remove the previous version first, then install.

  Plain Inno Setup would copy the new files over the old ones, which leaves
  files behind that the new version no longer ships. Instead, right before
  installing, we look for an earlier install under the same AppId and run its
  own uninstaller silently. That also catches an earlier copy that was put in
  a different folder or installed for all users instead of just this user.

  What survives: %APPDATA%\2x2Explorer (settings and rename log). The wizard
  reads the previous install folder, the per-user / all-users choice and the
  desktop-icon task when it starts, before this runs, so those carry over too.
  What goes: everything inside the old install folder, debug.log included.
  --------------------------------------------------------------------------- }

const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppGuid}_is1';

{ Where the previous version registered itself. Per-user installs write to
  HKCU, all-users installs to the 64-bit HKLM. HKLM32 is checked in case an
  old build was ever made without 64-bit install mode. }
function FindOldUninstaller(var RootKey: Integer; var Uninstaller: String): Boolean;
begin
  Result := True;

  RootKey := HKCU;
  if RegQueryStringValue(RootKey, UninstallKey, 'UninstallString', Uninstaller) then
    exit;

  RootKey := HKLM64;
  if RegQueryStringValue(RootKey, UninstallKey, 'UninstallString', Uninstaller) then
    exit;

  RootKey := HKLM32;
  if RegQueryStringValue(RootKey, UninstallKey, 'UninstallString', Uninstaller) then
    exit;

  Result := False;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  RootKey: Integer;
  Uninstaller: String;
  ResultCode: Integer;
  Waited: Integer;
begin
  Result := '';

  if not FindOldUninstaller(RootKey, Uninstaller) then
  begin
    Log('No previous version found. Fresh install.');
    exit;
  end;

  Uninstaller := RemoveQuotes(Uninstaller);

  { The entry can outlive its files when someone deleted the folder by hand.
    Nothing to remove then; the install below writes a fresh entry. }
  if not FileExists(Uninstaller) then
  begin
    Log('Previous version registered but its uninstaller is gone: ' + Uninstaller);
    exit;
  end;

  { Setup already asked at start, but the user may have opened it again
    since. A running copy keeps its files locked and the removal would stop
    halfway. }
  if CheckForMutexes('{#AppMutexNames}') then
  begin
    Result := CustomMessage('OldCopyRunning');
    exit;
  end;

  Log('Removing previous version: ' + Uninstaller);

  { An uninstaller for an all-users install asks for administrator rights
    (UAC) even when silent. If the user declines, Exec fails or the code is
    non-zero and we stop with a message instead of half-installing. }
  if not Exec(Uninstaller, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '',
              SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Result := FmtMessage(CustomMessage('OldRemoveFailed'), [IntToStr(ResultCode)])
      + #13#10 + SysErrorMessage(ResultCode);
    exit;
  end;

  if ResultCode <> 0 then
  begin
    Result := FmtMessage(CustomMessage('OldRemoveFailed'), [IntToStr(ResultCode)]);
    exit;
  end;

  { The uninstaller hands off to a copy of itself in %TEMP%. Wait for its
    registry entry to disappear so we do not start copying while it is still
    deleting. Give up quietly after 30 seconds; the install then overwrites. }
  Waited := 0;
  while RegKeyExists(RootKey, UninstallKey) and (Waited < 30000) do
  begin
    Sleep(250);
    Waited := Waited + 250;
  end;

  Log(Format('Previous version removed (waited %d ms).', [Waited]));
end;
