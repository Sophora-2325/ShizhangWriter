; novel-gm installer. Built by installer\build-installer.ps1, which stages files into dist\stage.

#define AppName "Shizhang Writer"
#define AppExe "ShizhangWriter.exe"
#ifndef AppVersion
  #define AppVersion "0.3.10"
#endif
#ifndef StageDir
  #define StageDir "..\dist\stage"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist\installer"
#endif

[Setup]
AppId={{7D3F2A61-9C4B-4E8A-B5D2-1F6E8C0A9B37}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Sophora-2325
AppPublisherURL=https://github.com/Sophora-2325/Auto-Novel-Writer
AppSupportURL=https://github.com/Sophora-2325/Auto-Novel-Writer/issues
VersionInfoVersion={#AppVersion}
; {autopf} is %LOCALAPPDATA%\Programs for "just me" and Program Files for "all users".
DefaultDirName={autopf}\novel-gm
DefaultGroupName={#AppName}
DisableDirPage=no
AppendDefaultDirName=yes
DirExistsWarning=auto
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
LicenseFile=..\LICENSE
SetupIconFile=..\apps\desktop-wpf\Assets\novel-gm.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputDir={#OutputDir}
OutputBaseFilename=ShizhangWriter-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
ShowLanguageDialog=auto

[Languages]
Name: "chs"; MessagesFile: "ChineseSimplified.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ja"; MessagesFile: "compiler:Languages\Japanese.isl"

[CustomMessages]
chs.SourceTreeDir=所选文件夹看起来是 novel-gm 的源码目录。%n安装会替换其中的 services 文件夹，请另选一个文件夹。
en.SourceTreeDir=The selected folder looks like a novel-gm source checkout.%nInstalling would replace its services folder. Please choose another folder.
ja.SourceTreeDir=選択したフォルダーは novel-gm のソースフォルダーのようです。%nインストールすると services フォルダーが置き換えられます。別のフォルダーを選んでください。

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[InstallDelete]
; Migrate the old launch file while retaining the established install location and AppId.
Type: files; Name: "{app}\novel-gm.exe"
Type: files; Name: "{autoprograms}\novel-gm.lnk"
Type: files; Name: "{autodesktop}\novel-gm.lnk"
; Replace backend code and runtime wholesale on upgrade so no stale modules linger.
Type: filesandordirs; Name: "{app}\services"
Type: filesandordirs; Name: "{app}\runtime"

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Python writes __pycache__ at runtime. User data lives in %LOCALAPPDATA%\novel-gm and is kept.
Type: filesandordirs; Name: "{app}\services"
Type: filesandordirs; Name: "{app}\runtime"

[Code]
// [InstallDelete] wipes {app}\services, so refuse a folder that holds a source checkout.
function IsSourceTree(Dir: String): Boolean;
begin
  Result := FileExists(AddBackslash(Dir) + 'services\core\main.py') and
            not FileExists(AddBackslash(Dir) + 'runtime\python\python.exe');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpSelectDir) and IsSourceTree(WizardDirValue) then
  begin
    MsgBox(CustomMessage('SourceTreeDir'), mbError, MB_OK);
    Result := False;
  end;
end;
