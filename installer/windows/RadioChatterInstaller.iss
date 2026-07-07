#ifndef AppVersion
#define AppVersion "0.1.0"
#endif

#ifndef PayloadDir
#define PayloadDir "..\..\dist\github\payload"
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
DisableProgramGroupPage=yes
OutputDir=..\..\dist
OutputBaseFilename=RadioChatter-{#AppVersion}-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "prepare_sidecar"; Description: "Prepare Pocket TTS sidecar now (requires Python 3.10+ and internet)"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\RadioChatter.dll"; DestDir: "{app}\BepInEx\plugins\RadioChatter"; Flags: ignoreversion
Source: "{#PayloadDir}\sidecar\*"; DestDir: "{app}\BepInEx\plugins\RadioChatter\sidecar"; Flags: ignoreversion recursesubdirs createallsubdirs

[Run]
Filename: "{app}\BepInEx\plugins\RadioChatter\sidecar\run_sidecar.bat"; Parameters: "--install-only"; WorkingDir: "{app}\BepInEx\plugins\RadioChatter\sidecar"; Description: "Prepare Pocket TTS sidecar"; Flags: postinstall runascurrentuser skipifsilent; Tasks: prepare_sidecar

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
