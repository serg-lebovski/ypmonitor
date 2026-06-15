; Inno Setup — установщик YPMonitor Агент.
; Собирается через ISCC с параметрами:
;   /DDistDir=...\dist\windows-agent   (содержит Ypmon.Agent.exe и *.ps1)
;   /DAppVer=1.1.1
;   /DOutDir=...\dist
; Тихая установка/обновление: YpmonAgent-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART

#ifndef AppVer
  #define AppVer "1.0.0"
#endif
#ifndef DistDir
  #define DistDir "..\..\dist\windows-agent"
#endif
#ifndef OutDir
  #define OutDir "..\..\dist"
#endif

[Setup]
AppId={{8B4E2C7A-9F31-4D2E-A6C1-0A1B2C3D4E5F}}
AppName=YPMonitor Агент
AppVersion={#AppVer}
AppPublisher=YPMonitor
DefaultDirName={commonpf}\ypmonitor
DefaultGroupName=YPMonitor
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=admin
OutputDir={#OutDir}
OutputBaseFilename=YpmonAgent-Setup
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
WizardStyle=modern
UninstallDisplayName=YPMonitor Агент

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "installservice"; Description: "Установить и запустить агент как службу Windows (рекомендуется)"; GroupDescription: "Служба:"

[Files]
Source: "{#DistDir}\Ypmon.Agent.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#DistDir}\install-service.ps1"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#DistDir}\uninstall-service.ps1"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Dirs]
; Папка приложения доступна на запись — агент хранит здесь config.json, state.json, snapshot.json
Name: "{app}"; Permissions: users-modify

[Icons]
Name: "{group}\Настройка YPMonitor Агент"; Filename: "{app}\Ypmon.Agent.exe"
Name: "{group}\Удалить YPMonitor Агент"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\Ypmon.Agent.exe"; Description: "Открыть окно настройки агента"; Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "sc.exe"; Parameters: "stop YpmonAgent"; Flags: runhidden; RunOnceId: "StopSvc"
Filename: "sc.exe"; Parameters: "delete YpmonAgent"; Flags: runhidden; RunOnceId: "DelSvc"

[Code]
const SVC = 'YpmonAgent';

function ServiceExists(): Boolean;
var rc: Integer;
begin
  Exec('sc.exe', 'query "' + SVC + '"', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Result := (rc = 0);
end;

// Перед копированием файлов останавливаем службу, чтобы освободить exe
function PrepareToInstall(var NeedsRestart: Boolean): String;
var rc: Integer;
begin
  Exec('sc.exe', 'stop "' + SVC + '"', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Sleep(2500);
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var rc: Integer; exe: String;
begin
  if CurStep = ssPostInstall then
  begin
    if WizardIsTaskSelected('installservice') then
    begin
      exe := ExpandConstant('{app}\Ypmon.Agent.exe');
      if not ServiceExists() then
        Exec('sc.exe', 'create "' + SVC + '" binPath= "' + exe + '" start= auto DisplayName= "YPMon Agent"',
             '', SW_HIDE, ewWaitUntilTerminated, rc);
      Exec('sc.exe', 'failure "' + SVC + '" reset= 60 actions= restart/5000/restart/5000/restart/5000',
           '', SW_HIDE, ewWaitUntilTerminated, rc);
      Exec('sc.exe', 'start "' + SVC + '"', '', SW_HIDE, ewWaitUntilTerminated, rc);
    end;
  end;
end;
