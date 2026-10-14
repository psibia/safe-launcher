; Установщик «Безопасного лаунчера» для Inno Setup 6.
;
; Собирать удобнее всего через build-installer.cmd (лежит рядом): он публикует лаунчер в папку publish,
; службу и ядро VPN — в папку publish-core, затем компилирует этот скрипт. Готовый установщик появится в папке Output.
;
; Лаунчер и служба публикуются как self-contained: на компьютере пользователя не нужно ставить .NET 8.

#define MyAppName "Безопасный лаунчер"
#define MyAppExeName "KillSwitchLauncher.exe"
#define MyPublishDir AddBackslash(SourcePath) + "publish"
#define MyCorePublishDir AddBackslash(SourcePath) + "publish-core"
#define ServiceName "SafeLauncherNet"
#define ServiceExe "SafeLauncher.Service.exe"
; Папка службы и ядра: только латиница и запись только для администраторов (см. core\README.md).
#define CoreDir "{commonpf64}\SafeLauncher\core"
; Мьютекс одной копии лаунчера (Services\SingleInstance.cs).
#define LauncherMutex "Local\KillSwitchLauncher.SingleInstance"

#if !FileExists(MyPublishDir + "\" + MyAppExeName)
  #error "Нет publish\KillSwitchLauncher.exe. Сначала запустите build-installer.cmd (или dotnet publish, см. README)."
#endif
#if !FileExists(MyCorePublishDir + "\" + ServiceExe) || !FileExists(MyCorePublishDir + "\launcher-core.exe")
  #error "Нет службы или ядра в publish-core. Сначала запустите build-installer.cmd."
#endif

; Версия берётся из собранного exe (свойство <Version> в KillSwitchLauncher.csproj)
#define MyAppVersion GetVersionNumbersString(MyPublishDir + "\" + MyAppExeName)

[Setup]
; AppId не менять: по нему установщик находит прошлую версию и обновляет её
AppId={{63FAD8AB-FDB1-4442-8A82-DCB83BC08EF2}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
VersionInfoVersion={#MyAppVersion}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\KillSwitchLauncher\Assets\logo.ico
OutputDir=Output
OutputBaseFilename=SafeLauncher-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; С версии 2.0 лаунчер ставится для всех пользователей: вместе с ним ставится служба Windows
; «Безопасный лаунчер — сеть» (ядро VPN, фильтры трафика), а для этого нужны права администратора.
PrivilegesRequired=admin
; Ключ автозапуска в HKCU удаляется при удалении (см. [Registry]) — это намеренно.
UsedUserAreasWarning=no

; .NET 8 работает на Windows 10 1809 и новее, только 64-битные системы
MinVersion=10.0.17763
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Если лаунчер запущен, установщик предложит закрыть его перед обновлением/удалением
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Служба и ядро. Перед заменой служба останавливается (PrepareToInstall), после — регистрируется заново (ssPostInstall).
Source: "{#MyCorePublishDir}\*"; DestDir: "{#CoreDir}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Служба с версии 2.0 — один exe. Файлы прежней службы из нескольких файлов (её ставил tools\update-service) убираются.
Type: files; Name: "{#CoreDir}\*.dll"
Type: files; Name: "{#CoreDir}\*.json"
Type: files; Name: "{#CoreDir}\*.pdb"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; «Запускать вместе с Windows» лаунчер пишет сам в HKCU\…\Run; при удалении эта запись убирается.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "SafeLauncher"; Flags: uninsdeletevalue dontcreatekey

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent runasoriginaluser
; Обновление из самого лаунчера: он запускает установщик с /SILENT /autoupdate=1 и закрывается,
; а после установки новая версия запускается здесь (от имени обычного пользователя, даже если установка шла с правами администратора).
Filename: "{app}\{#MyAppExeName}"; Flags: nowait runasoriginaluser skipifnotsilent; Check: IsAutoUpdate

[UninstallDelete]
; Рабочая папка ядра (её создаёт служба), затем пустые папки службы. Порядок важен.
Type: filesandordirs; Name: "{#CoreDir}\run"
Type: dirifempty; Name: "{#CoreDir}"
Type: dirifempty; Name: "{commonpf64}\SafeLauncher"
; Журналы службы и ядра (%ProgramData%\SafeLauncher\logs: журналы, очищенные конфиги ядра, описания падений).
Type: filesandordirs; Name: "{commonappdata}\SafeLauncher\logs"
Type: dirifempty; Name: "{commonappdata}\SafeLauncher"

; Настройки пользователя (%LOCALAPPDATA%\KillSwitchLauncher\settings.json) при удалении намеренно
; не трогаем: при переустановке список приложений, подключения и правила VPN сохранятся.
; Журналы лаунчера (%LOCALAPPDATA%\KillSwitchLauncher\logs) остаются вместе с настройками: их срок хранения ограничен.

[Code]
const
  // Inno Setup добавляет к AppId «_is1»: так называется запись об установке в «Программы и компоненты».
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{63FAD8AB-FDB1-4442-8A82-DCB83BC08EF2}_is1';

// Установщик запущен лаунчером для обновления (параметр /autoupdate=1).
function IsAutoUpdate: Boolean;
begin
  Result := ExpandConstant('{param:autoupdate|0}') = '1';
end;

// Версии 1.x по умолчанию ставились только для текущего пользователя (в AppData). Теперь лаунчер ставится
// для всех, поэтому такую папку (её передаёт обновляемый лаунчер через /DIR) заменяем на Program Files.
procedure InitializeWizard;
begin
  if Pos('\APPDATA\', Uppercase(AddBackslash(WizardForm.DirEdit.Text))) > 0 then
    WizardForm.DirEdit.Text := ExpandConstant('{autopf}\{#MyAppName}');
end;

// Лаунчер должен быть закрыт: он держит свои файлы и связь со службой. Обновляемый лаунчер закрывается сам
// сразу после запуска установщика, поэтому сначала немного ждём.
function WaitForLauncherToClose(Seconds: Integer): Boolean;
var
  I: Integer;
begin
  I := 0;
  while CheckForMutexes('{#LauncherMutex}') and (I < Seconds * 2) do
  begin
    Sleep(500);
    I := I + 1;
  end;
  Result := not CheckForMutexes('{#LauncherMutex}');
end;

procedure StopService;
var
  ResultCode: Integer;
begin
  // Нет службы (первая установка) — net stop просто вернёт ошибку.
  Exec(ExpandConstant('{sys}\net.exe'), 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  // Процесс службы и ядра может завершаться ещё мгновение после остановки.
  Sleep(1000);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#ServiceExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM launcher-core.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// Убрать установку 1.x «только для меня» (в AppData): иначе останутся две копии лаунчера и два ярлыка.
// Настройки пользователя при этом сохраняются.
procedure RemovePerUserCopy;
var
  Uninstaller, Location: String;
  ResultCode: Integer;
begin
  if not RegQueryStringValue(HKCU, UninstallKey, 'UninstallString', Uninstaller) then
    Exit;
  if RegQueryStringValue(HKCU, UninstallKey, 'InstallLocation', Location) and
     (CompareText(RemoveBackslash(Location), RemoveBackslash(WizardDirValue)) = 0) then
    Exit;
  Uninstaller := RemoveQuotes(Uninstaller);
  if FileExists(Uninstaller) then
    Exec(Uninstaller, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not WaitForLauncherToClose(20) then
  begin
    Result := 'Закройте «{#MyAppName}» (значок в трее → «Закрыть лаунчер») и запустите установку снова.';
    Exit;
  end;
  StopService;
  RemovePerUserCopy;
end;

// Служба регистрируется на новый exe и запускается. Не вышло — лаунчер работает с системным VPN,
// а подключения лаунчера и блокировка трафика мимо VPN скажут, что служба недоступна.
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep <> ssPostInstall then
    Exit;
  if not Exec(ExpandConstant('{#CoreDir}\{#ServiceExe}'), '--install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
    SuppressibleMsgBox('Не удалось установить службу «Безопасный лаунчер — сеть» (код ' + IntToStr(ResultCode) + ').' + #13#10#13#10 +
      'Лаунчер будет работать с вашей VPN-программой, но подключения лаунчера и блокировка трафика мимо VPN ' +
      'работать не будут. Попробуйте переустановить лаунчер.', mbError, MB_OK, IDOK);
end;

// Удаление: служба останавливается (ядро вместе с ней) и удаляется до удаления файлов. Фильтры Windows лаунчер
// ставит только на время работы, поэтому после удаления в системе ничего не остаётся.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep <> usUninstall then
    Exit;
  Exec(ExpandConstant('{#CoreDir}\{#ServiceExe}'), '--uninstall', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  StopService; // если служба не удалилась, файлы всё равно должны освободиться
end;

// Удаление: лаунчер должен быть закрыт, иначе его файлы останутся.
function InitializeUninstall: Boolean;
begin
  Result := True;
  while CheckForMutexes('{#LauncherMutex}') do
    if SuppressibleMsgBox('Закройте «{#MyAppName}» (значок в трее → «Закрыть лаунчер») и нажмите «Повтор».',
         mbError, MB_RETRYCANCEL, IDCANCEL) = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
end;
