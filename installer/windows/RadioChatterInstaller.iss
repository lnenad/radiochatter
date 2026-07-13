#ifndef AppVersion
#define AppVersion "0.1.0"
#endif

#ifndef PayloadDir
#define PayloadDir "..\..\dist\github\payload"
#endif

#ifndef OutputDir
#define OutputDir "..\..\dist"
#endif

#ifdef BundledModels
#define InstallerSuffix "Setup-WithModels"
#else
#define InstallerSuffix "Setup"
#endif

[Setup]
AppId={{8F4C7C7B-2D2F-4B7A-A90E-2FA01DAB4AF0}
AppName=RadioChatter
AppVersion={#AppVersion}
AppPublisher=lnenad
AppPublisherURL=https://github.com/lnenad/radiochatter
AppSupportURL=https://github.com/lnenad/radiochatter/issues
AppUpdatesURL=https://github.com/lnenad/radiochatter/releases
DefaultDirName={code:GetDefaultGameDir}
AppendDefaultDirName=no
DisableDirPage=no
UsePreviousAppDir=no
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=RadioChatter-{#AppVersion}-{#InstallerSuffix}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
#ifdef BundledModels
Name: "prepare_sidecar"; Description: "Prepare voice sidecar dependencies now (models are bundled; internet may still be needed for Python packages)"; Flags: unchecked
#else
Name: "prepare_sidecar"; Description: "Prepare voice sidecar now (requires internet for Python packages and model downloads)"; Flags: unchecked
#endif

[Files]
Source: "{#PayloadDir}\RadioChatter.dll"; DestDir: "{app}\BepInEx\plugins\RadioChatter"; Flags: ignoreversion
Source: "{#PayloadDir}\sidecar\server.py"; DestDir: "{app}\BepInEx\plugins\RadioChatter\sidecar"; Flags: ignoreversion
Source: "{#PayloadDir}\sidecar\requirements.txt"; DestDir: "{app}\BepInEx\plugins\RadioChatter\sidecar"; Flags: ignoreversion
Source: "{#PayloadDir}\sidecar\voices.json"; DestDir: "{app}\BepInEx\plugins\RadioChatter\sidecar"; Flags: ignoreversion
Source: "{#PayloadDir}\sidecar\run_sidecar.bat"; DestDir: "{app}\BepInEx\plugins\RadioChatter\sidecar"; Flags: ignoreversion
Source: "{#PayloadDir}\sidecar\run_sidecar.sh"; DestDir: "{app}\BepInEx\plugins\RadioChatter\sidecar"; Flags: ignoreversion
#ifdef BundledModels
Source: "{#PayloadDir}\sidecar\cache\*"; DestDir: "{localappdata}\RadioChatter\cache"; Flags: ignoreversion recursesubdirs createallsubdirs
#endif

[Run]
Filename: "{cmd}"; Parameters: "/C """"{app}\BepInEx\plugins\RadioChatter\sidecar\run_sidecar.bat"" --install-only"""; WorkingDir: "{app}\BepInEx\plugins\RadioChatter\sidecar"; Description: "Prepare Pocket TTS sidecar"; StatusMsg: "Preparing Pocket TTS sidecar. This can take several minutes on first install..."; Flags: postinstall runascurrentuser skipifsilent waituntilterminated; Tasks: prepare_sidecar

[Code]
function FirstExistingDir(Path1: string; Path2: string; Path3: string): string;
begin
  if DirExists(Path1) then
    Result := Path1
  else if DirExists(Path2) then
    Result := Path2
  else if DirExists(Path3) then
    Result := Path3
  else
    Result := Path1;
end;

function GetDefaultGameDir(Param: string): string;
begin
  Result := FirstExistingDir(
    'D:\SteamLibrary\steamapps\common\Nuclear Option',
    ExpandConstant('{pf}\Steam\steamapps\common\Nuclear Option'),
    ExpandConstant('{pf32}\Steam\steamapps\common\Nuclear Option'));
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  GameDir: string;
begin
  Result := True;
  if CurPageID <> wpSelectDir then
    exit;

  GameDir := ExpandConstant('{app}');
  if not DirExists(GameDir) then
  begin
    MsgBox('Select the Nuclear Option game folder, not a new installer folder.', mbError, MB_OK);
    Result := False;
    exit;
  end;

  if (not FileExists(GameDir + '\NuclearOption.exe')) and
     (not DirExists(GameDir + '\NuclearOption_Data')) then
  begin
    if MsgBox('This folder does not look like a Nuclear Option install. Continue anyway?', mbConfirmation, MB_YESNO) = IDNO then
    begin
      Result := False;
      exit;
    end;
  end;

  if not DirExists(GameDir + '\BepInEx') then
  begin
    MsgBox('BepInEx was not found in this game folder. Install BepInEx 5 for Nuclear Option first, then run this installer again.', mbError, MB_OK);
    Result := False;
  end;
end;
