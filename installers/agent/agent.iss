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

var
  ServerPage: TInputQueryWizardPage;   // адрес сервера + API-ключ
  SvcPage: TInputQueryWizardPage;      // учётная запись службы

// Мастер: страницы ввода параметров подключения и учётной записи службы.
procedure InitializeWizard();
begin
  ServerPage := CreateInputQueryPage(wpSelectTasks,
    'Подключение к серверу YPMonitor',
    'Куда агент будет отправлять отчёты',
    'Укажите адрес сервера и API-ключ (их можно взять в веб-интерфейсе сервера на странице сервера клиента). Поля можно оставить пустыми и заполнить позже в окне агента.');
  ServerPage.Add('Адрес сервера (например http://10.10.20.25:8081):', False);
  ServerPage.Add('API-ключ сервера:', False);

  SvcPage := CreateInputQueryPage(ServerPage.ID,
    'Учётная запись службы Windows',
    'Под какой учётной записью запускать службу агента',
    'Оставьте поля пустыми для запуска от системной учётной записи (LocalSystem). ' +
    'Либо укажите доменную/локальную учётную запись в формате DOMAIN\User (у неё должно быть право «Вход в качестве службы»).');
  SvcPage.Add('Учётная запись (DOMAIN\User, пусто = LocalSystem):', False);
  SvcPage.Add('Пароль:', True);
end;

// Страницу учётной записи показываем только если выбрана установка службы.
function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if PageID = SvcPage.ID then
    Result := not WizardIsTaskSelected('installservice');
end;

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

// Записываем адрес сервера/ключ в config.json через сам агент (--provision).
procedure ProvisionConfig();
var rc: Integer; exe, args, srv, key: String;
begin
  srv := Trim(ServerPage.Values[0]);
  key := Trim(ServerPage.Values[1]);
  if (srv = '') and (key = '') then exit;
  exe := ExpandConstant('{app}\Ypmon.Agent.exe');
  args := '--provision';
  if srv <> '' then args := args + ' --server "' + srv + '"';
  if key <> '' then args := args + ' --apikey "' + key + '"';
  args := args + ' --service "' + SVC + '"';
  Exec(exe, args, '', SW_HIDE, ewWaitUntilTerminated, rc);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var rc: Integer; exe, acct, svcUser, svcPass: String;
begin
  if CurStep = ssPostInstall then
  begin
    // 1) Записываем настройки подключения в config.json
    ProvisionConfig();

    // 2) Устанавливаем/обновляем службу
    if WizardIsTaskSelected('installservice') then
    begin
      exe := ExpandConstant('{app}\Ypmon.Agent.exe');
      svcUser := Trim(SvcPage.Values[0]);
      svcPass := SvcPage.Values[1];
      if svcUser <> '' then
        acct := ' obj= "' + svcUser + '" password= "' + svcPass + '"'
      else
        acct := ' obj= "LocalSystem"';

      if ServiceExists() then
        // служба уже есть (возможно, старая папка) — переключаем путь и учётную запись
        Exec('sc.exe', 'config "' + SVC + '" binPath= "' + exe + '" start= auto' + acct,
             '', SW_HIDE, ewWaitUntilTerminated, rc)
      else
        Exec('sc.exe', 'create "' + SVC + '" binPath= "' + exe + '" start= auto DisplayName= "YPMon Agent"' + acct,
             '', SW_HIDE, ewWaitUntilTerminated, rc);
      Exec('sc.exe', 'failure "' + SVC + '" reset= 60 actions= restart/5000/restart/5000/restart/5000',
           '', SW_HIDE, ewWaitUntilTerminated, rc);
      Exec('sc.exe', 'start "' + SVC + '"', '', SW_HIDE, ewWaitUntilTerminated, rc);
    end;
  end;
end;
