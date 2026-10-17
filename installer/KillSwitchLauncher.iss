; Установщик «Безопасного лаунчера» для Inno Setup 6.
;
; Собирать удобнее всего через build-installer.cmd (лежит рядом): он публикует лаунчер в папку publish,
; службу и ядро VPN — в папку publish-core, затем компилирует этот скрипт. Готовый установщик появится в папке Output.
;
; Лаунчер и служба публикуются как self-contained: на компьютере пользователя не нужно ставить .NET 8.
;
; Лаунчер ставится по ключу лицензии. Хеши ключей берутся из license-hashes.txt рядом со скриптом,
; в GitHub этого файла нет (см. «Ключ лицензии» в docs\DETAILS.md).

#define MyAppName "Безопасный лаунчер"
#define MyAppExeName "KillSwitchLauncher.exe"
#define MyPublishDir AddBackslash(SourcePath) + "publish"
#define MyCorePublishDir AddBackslash(SourcePath) + "publish-core"
#define ServiceName "SafeLauncherNet"
#define ServiceExe "SafeLauncher.Service.exe"
; Папка службы и ядра: только латиница и запись только для администраторов (см. core\README.md).
#define CoreDir "{commonpf64}\SafeLauncher\core"
; Папка обновления через службу: установщик новой версии, резервная копия для отката, ход установки (служба, UpdateRunner.cs).
#define UpdateDir "{commonpf64}\SafeLauncher\update"
; Мьютекс одной копии лаунчера (Services\SingleInstance.cs).
#define LauncherMutex "Local\KillSwitchLauncher.SingleInstance"
// Параметр, которым установщик просит лаунчер сохранить ключ лицензии (Services\License.cs, StoreArgument).
#define LicenseStoreArg "--store-license"

#if !FileExists(MyPublishDir + "\" + MyAppExeName)
  #error "Нет publish\KillSwitchLauncher.exe. Сначала запустите build-installer.cmd (или dotnet publish, см. README)."
#endif
#if !FileExists(MyCorePublishDir + "\" + ServiceExe) || !FileExists(MyCorePublishDir + "\launcher-core.exe")
  #error "Нет службы или ядра в publish-core. Сначала запустите build-installer.cmd."
#endif

; Версия берётся из собранного exe (свойство <Version> в KillSwitchLauncher.csproj)
#define MyAppVersion GetVersionNumbersString(MyPublishDir + "\" + MyAppExeName)

; Хеши ключей лицензии: SHA-256 ключа, записанного заглавными буквами без дефисов, по одному в строке.
; Строки с «;» считаются комментариями. В установщик попадают только хеши, самих ключей в нём нет.
#define LicenseHashesFile AddBackslash(SourcePath) + "license-hashes.txt"
#if !FileExists(LicenseHashesFile)
  #error Нет installer\license-hashes.txt с хешами ключей лицензии. Этого файла нет в GitHub: положите его рядом со скриптом.
#endif
#define LicenseHashes "|"
#define HashFile
#define HashLine
#sub ReadLicenseHash
  #expr HashLine = Trim(FileRead(HashFile))
  #if HashLine != "" && Pos(";", HashLine) == 0
    #if Len(HashLine) != 64
      #expr Error("В license-hashes.txt строка «" + HashLine + "» не похожа на хеш SHA-256 (нужно 64 знака).")
    #endif
    #expr LicenseHashes = LicenseHashes + LowerCase(HashLine) + "|"
  #endif
#endsub
#for {HashFile = FileOpen(LicenseHashesFile); HashFile && !FileEof(HashFile); ""} ReadLicenseHash
#if HashFile
  #expr FileClose(HashFile)
#endif
#if LicenseHashes == "|"
  #error В installer\license-hashes.txt нет ни одного хеша ключа лицензии.
#endif

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
; Остатки обновлений через службу. При обновлении через службу папку не трогаем: в ней резервная копия для отката.
Type: filesandordirs; Name: "{#UpdateDir}"; Check: not IsServiceUpdate

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
; При обновлении через службу (/service=1) лаунчер запускает окно ожидания обновления, когда помощник службы закончит.
Filename: "{app}\{#MyAppExeName}"; Flags: nowait runasoriginaluser skipifnotsilent; Check: IsAutoUpdate and not IsServiceUpdate

[UninstallDelete]
; Рабочая папка ядра (её создаёт служба), затем пустые папки службы. Порядок важен.
Type: filesandordirs; Name: "{#CoreDir}\run"
Type: dirifempty; Name: "{#CoreDir}"
Type: filesandordirs; Name: "{#UpdateDir}"
Type: dirifempty; Name: "{commonpf64}\SafeLauncher"
; Журналы службы и ядра (%ProgramData%\SafeLauncher\logs: журналы, очищенные конфиги ядра, описания падений).
Type: filesandordirs; Name: "{commonappdata}\SafeLauncher\logs"
Type: dirifempty; Name: "{commonappdata}\SafeLauncher"

; Настройки и журналы пользователей (%LOCALAPPDATA%\KillSwitchLauncher) удаляются, только если отметить их
; в окне удаления (AskWhatToRemove в [Code]). По умолчанию они остаются: при переустановке всё сохранится.
; Здесь их не перечислить: записи этого раздела не зависят от выбора при удалении.

[Code]
const
  // Inno Setup добавляет к AppId «_is1»: так называется запись об установке в «Программы и компоненты».
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{63FAD8AB-FDB1-4442-8A82-DCB83BC08EF2}_is1';
  // Ключ лицензии хранит и проверяет сам лаунчер (Services\License.cs). Установщик записывает его сюда через
  // «KillSwitchLauncher.exe --store-license», зашифрованным для этого компьютера. Значение есть — ключ уже введён.
  LicenseRegKey = 'Software\SafeLauncher';
  LicenseRegValue = 'LicenseKey';
  // Профили пользователей Windows: где искать их настройки при удалении.
  ProfileListKey = 'SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList';

var
  LicensePage: TInputQueryWizardPage;
  LicenseKey: String;       // принятый ключ лицензии (установщик передаст его лаунчеру для сохранения)
  LicenseKnown: Boolean;    // ключ уже есть (сохранён прошлой установкой или принят сейчас): страница ключа не нужна
  Installed: Boolean;       // файлы новой версии поставлены
  LauncherRestarted: Boolean; // обновление из лаунчера не состоялось, и прежний лаунчер уже запущен обратно
  RemoveSettings, RemoveLogs: Boolean; // выбор в окне удаления

// Установщик запущен лаунчером для обновления (параметр /autoupdate=1).
function IsAutoUpdate: Boolean;
begin
  Result := ExpandConstant('{param:autoupdate|0}') = '1';
end;

// Обновление ставит служба (/autoupdate=1 /service=1): установщик работает от имени системы без окон, его запускает
// помощник службы (UpdateRunner.cs) и сам проверяет результат, а лаунчер потом запускает окно ожидания обновления.
// Параметр передаёт помощник прежней версии, поэтому его нужно поддерживать и в следующих версиях.
function IsServiceUpdate: Boolean;
begin
  Result := IsAutoUpdate and (ExpandConstant('{param:service|0}') = '1');
end;

// ---------------------------------------------------------------- окна

// Сигнатура CreateCustomForm поменялась в Inno Setup 6.6.0: размер окна задаётся при создании.
// До 6.6.0 окно при показе расширяется по WizardSizePercent, поэтому у элементов заданы привязки (Anchors).
function CreateDialogForm(const Width, Height: Integer): TSetupForm;
begin
#if Ver >= EncodeVer(6, 6, 0)
  Result := CreateCustomForm(Width, Height, True, True);
#else
  Result := CreateCustomForm;
  Result.ClientWidth := Width;
  Result.ClientHeight := Height;
  Result.KeepSizeY := True;
#endif
end;

function AddText(const Form: TSetupForm; const Caption: String; const Left, Top, Width: Integer): TNewStaticText;
begin
  Result := TNewStaticText.Create(Form);
  Result.Parent := Form;
  Result.AutoSize := False;
  Result.WordWrap := True;
  Result.SetBounds(Left, Top, Width, ScaleY(15));
  Result.Anchors := [akLeft, akTop, akRight];
  Result.Caption := Caption;
  Result.AdjustHeight;
end;

function AddCheckBox(const Form: TSetupForm; const Caption: String; const Left, Top, Width: Integer): TNewCheckBox;
begin
  Result := TNewCheckBox.Create(Form);
  Result.Parent := Form;
  Result.SetBounds(Left, Top, Width, ScaleY(17));
  Result.Anchors := [akLeft, akTop, akRight];
  Result.Caption := Caption;
end;

// Кнопки «ОК» и «Отмена» в правом нижнем углу окна. Возвращает кнопку «ОК».
function AddButtons(const Form: TSetupForm; const OkCaption: String): TNewButton;
var
  CancelButton: TNewButton;
  Width: Integer;
begin
  Width := Form.CalculateButtonWidth([OkCaption, SetupMessage(msgButtonCancel)]);
  CancelButton := TNewButton.Create(Form);
  CancelButton.Parent := Form;
  CancelButton.Caption := SetupMessage(msgButtonCancel);
  CancelButton.ModalResult := mrCancel;
  CancelButton.Cancel := True;
  CancelButton.SetBounds(Form.ClientWidth - ScaleX(16) - Width, Form.ClientHeight - ScaleY(12 + 23), Width, ScaleY(23));
  CancelButton.Anchors := [akRight, akBottom];
  Result := TNewButton.Create(Form);
  Result.Parent := Form;
  Result.Caption := OkCaption;
  Result.ModalResult := mrOk;
  Result.Default := True;
  Result.SetBounds(CancelButton.Left - ScaleX(8) - Width, CancelButton.Top, Width, ScaleY(23));
  Result.Anchors := [akRight, akBottom];
end;

// ---------------------------------------------------------------- ключ лицензии

// SHA-256 ключа, записанного заглавными латинскими буквами без дефисов и пробелов (так получены хеши в license-hashes.txt).
function KeyHash(const Key: String): String;
var
  I, Code: Integer;
  Clean: String;
begin
  Clean := '';
  for I := 1 to Length(Key) do
  begin
    Code := Ord(Key[I]);
    if (Code >= Ord('a')) and (Code <= Ord('z')) then
      Code := Code - 32;
    if ((Code >= Ord('A')) and (Code <= Ord('Z'))) or ((Code >= Ord('0')) and (Code <= Ord('9'))) then
      Clean := Clean + Chr(Code);
  end;
  Result := '';
  if Clean <> '' then
    Result := Lowercase(GetSHA256OfString(Clean));
end;

function IsKnownHash(const Hash: String): Boolean;
begin
  Result := (Length(Hash) = 64) and (Pos('|' + Hash + '|', '{#LicenseHashes}') > 0);
end;

// Ключ подходит, если его хеш есть во встроенном списке (license-hashes.txt).
function KeyIsKnown(const Key: String): Boolean;
begin
  Result := IsKnownHash(KeyHash(Key));
end;

// На компьютере уже сохранён ключ лицензии (его записала прошлая установка). Тогда обновление не спрашивает ключ:
// лаунчер проверит его сам при запуске.
function MachineHasKey: Boolean;
begin
  Result := RegValueExists(HKLM64, LicenseRegKey, LicenseRegValue);
end;

// Обновление из лаунчера не состоялось: лаунчер закрылся ради установки, поэтому запускаем его обратно.
// Он сам сообщит, что обновиться не удалось.
procedure RestartOldLauncher;
var
  Dir: String;
  ResultCode: Integer;
begin
  LauncherRestarted := True;
  Dir := ExpandConstant('{param:DIR|}');
  if (Dir = '') or not FileExists(AddBackslash(Dir) + '{#MyAppExeName}') then
    Exit;
  try
    ExecAsOriginalUser(AddBackslash(Dir) + '{#MyAppExeName}', '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
  except
    Log('Не удалось запустить лаунчер обратно: ' + GetExceptionMessage);
  end;
end;

// Ключ берётся из записи прошлой установки или из параметра /KEY=. Если его нет, при установке с окнами ключ
// спросит страница «Ключ лицензии», а при обновлении из лаунчера — сам лаунчер при первом запуске (он проверяет
// ключ всегда). При тихой установке не из лаунчера нужен /KEY.
function InitializeSetup: Boolean;
var
  Key: String;
begin
  Result := True;
  LicenseKey := '';
  LicenseKnown := MachineHasKey;
  if LicenseKnown then
    Exit; // ключ уже сохранён прошлой установкой: лаунчер проверит его сам
  Key := ExpandConstant('{param:KEY|}');
  if Key <> '' then
  begin
    if KeyIsKnown(Key) then
    begin
      LicenseKey := Key;
      LicenseKnown := True;
    end
    else if WizardSilent then
    begin
      SuppressibleMsgBox('Ключ лицензии из параметра /KEY не подошёл.', mbCriticalError, MB_OK, IDOK);
      Result := False;
    end;
    Exit;
  end;
  // Ключа нет. При установке с окнами его спросит страница мастера; при обновлении из лаунчера ключ при первом
  // запуске спросит сам лаунчер. При тихой установке не из лаунчера нужен /KEY.
  if WizardSilent and not IsAutoUpdate then
  begin
    SuppressibleMsgBox('Для установки нужен ключ лицензии. При установке без окон передайте его параметром ' +
      '/KEY=XXXXX-XXXXX-XXXXX-XXXXX-XXXXX.', mbCriticalError, MB_OK, IDOK);
    Result := False;
  end;
end;

procedure InitializeWizard;
begin
  // Версии 1.x по умолчанию ставились только для текущего пользователя (в AppData). Теперь лаунчер ставится
  // для всех, поэтому такую папку (её передаёт обновляемый лаунчер через /DIR) заменяем на Program Files.
  if Pos('\APPDATA\', Uppercase(AddBackslash(WizardForm.DirEdit.Text))) > 0 then
    WizardForm.DirEdit.Text := ExpandConstant('{autopf}\{#MyAppName}');

  LicensePage := CreateInputQueryPage(wpWelcome, 'Ключ лицензии',
    'Для установки и использования программного обеспечения SafeLauncher требуется действующая лицензия.',
    'Введите ключ лицензии:');
  LicensePage.Add('', False);
  LicensePage.Values[0] := ExpandConstant('{param:KEY|}');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  // Страница ключа не нужна, если ключ уже известен. В тихом режиме её тоже пропускаем: ввести ключ там нельзя,
  // иначе установка зависла бы, ожидая ввод. Ключ тогда либо передан в /KEY (проверено в InitializeSetup),
  // либо это обновление из лаунчера, и ключ при первом запуске спросит сам лаунчер.
  Result := (PageID = LicensePage.ID) and (LicenseKnown or WizardSilent);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID <> LicensePage.ID then
    Exit;
  if KeyIsKnown(LicensePage.Values[0]) then
    LicenseKey := LicensePage.Values[0]
  else
  begin
    MsgBox('Ключ не подошёл. Проверьте, что он введён полностью и латинскими буквами: ' +
      'пять групп по пять знаков через дефис.', mbError, MB_OK);
    Result := False;
  end;
end;

// ---------------------------------------------------------------- установка

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

// Передаёт принятый ключ лаунчеру: тот зашифрует его для этого компьютера и запишет в HKLM (нужны права
// администратора, они у установщика есть). Не вышло — лаунчер спросит ключ при первом запуске.
procedure StoreLicenseKey(const Key: String);
var
  ResultCode: Integer;
begin
  if not Exec(ExpandConstant('{app}\{#MyAppExeName}'), '{#LicenseStoreArg} "' + Key + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Log('Не удалось запустить лаунчер для сохранения ключа лицензии.');
end;

// Служба регистрируется на новый exe и запускается. Не вышло — лаунчер работает с системным VPN,
// а подключения лаунчера и блокировка трафика мимо VPN скажут, что служба недоступна.
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep <> ssPostInstall then
    Exit;
  Installed := True;
  // Ключ лицензии: лаунчер хранит и проверяет его сам. Передаём принятый ключ, чтобы при запуске он не спрашивался.
  if LicenseKey <> '' then
    StoreLicenseKey(LicenseKey);
  if not Exec(ExpandConstant('{#CoreDir}\{#ServiceExe}'), '--install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
    SuppressibleMsgBox('Не удалось установить службу «Безопасный лаунчер — сеть» (код ' + IntToStr(ResultCode) + ').' + #13#10#13#10 +
      'Лаунчер будет работать с вашей VPN-программой, но подключения лаунчера и блокировка трафика мимо VPN ' +
      'работать не будут. Попробуйте переустановить лаунчер.', mbError, MB_OK, IDOK);
end;

procedure DeinitializeSetup;
begin
  // При обновлении через службу лаунчер запустит окно ожидания обновления, когда помощник службы закончит.
  if IsAutoUpdate and not IsServiceUpdate and not Installed and not LauncherRestarted then
    RestartOldLauncher;
end;

// ---------------------------------------------------------------- удаление

// Окно удаления: что удалить вместе с лаунчером. Показывается после вопроса «Вы действительно хотите удалить…?»,
// галочки по умолчанию сняты. False: пользователь нажал «Отмена» и удалять передумал.
function AskWhatToRemove: Boolean;
var
  Form: TSetupForm;
  Note: TNewStaticText;
  SettingsBox, LogsBox: TNewCheckBox;
  Margin, Indent, TextWidth, Top: Integer;
begin
  Form := CreateDialogForm(ScaleX(460), ScaleY(262));
  try
    Form.Caption := FmtMessage(SetupMessage(msgUninstallAppFullTitle), ['{#MyAppName}']);
    Margin := ScaleX(16);
    Indent := ScaleX(18);
    TextWidth := Form.ClientWidth - 2 * Margin;

    Note := AddText(Form, 'Лаунчер, его служба и ядро VPN будут удалены. Ключ лицензии тоже удалится. ' +
      'Остальные данные можно удалить вместе с ними или оставить на случай повторной установки.', Margin, ScaleY(16), TextWidth);
    Top := Note.Top + Note.Height + ScaleY(14);

    SettingsBox := AddCheckBox(Form, 'Удалить настройки', Margin, Top, TextWidth);
    Note := AddText(Form, 'Приложения, подключения и подписки всех пользователей компьютера.',
      Margin + Indent, Top + ScaleY(20), TextWidth - Indent);
    Top := Note.Top + Note.Height + ScaleY(12);

    LogsBox := AddCheckBox(Form, 'Удалить журналы', Margin, Top, TextWidth);
    AddText(Form, 'Журнал событий и технический журнал лаунчера всех пользователей компьютера.',
      Margin + Indent, Top + ScaleY(20), TextWidth - Indent);

    AddButtons(Form, 'Удалить');
    Result := Form.ShowModal = mrOk;
    RemoveSettings := SettingsBox.Checked;
    RemoveLogs := LogsBox.Checked;
  finally
    Form.Free;
  end;
end;

// В ProfileImagePath встречаются переменные окружения (%SystemDrive%, %SystemRoot%).
procedure ExpandEnvVar(var S: String; const Name: String);
var
  P: Integer;
begin
  P := Pos('%' + Uppercase(Name) + '%', Uppercase(S));
  if P > 0 then
    S := Copy(S, 1, P - 1) + GetEnv(Name) + Copy(S, P + Length(Name) + 2, Length(S));
end;

// Папка данных лаунчера одного пользователя: удаляется то, что отмечено в окне удаления. Журналы здесь:
// журнал событий (events.log) и технический журнал с отчётами о сбоях (папка logs). Всё остальное относится к настройкам.
procedure CleanDataFolder(const Dir: String);
var
  FindRec: TFindRec;
  Names: TStringList;
  I: Integer;
  IsLog: Boolean;
begin
  if not DirExists(Dir) then
    Exit;
  if RemoveSettings and RemoveLogs then
  begin
    DelTree(Dir, True, True, True);
    Exit;
  end;
  Names := TStringList.Create;
  try
    if FindFirst(AddBackslash(Dir) + '*', FindRec) then
      try
        repeat
          if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
            Names.Add(FindRec.Name);
        until not FindNext(FindRec);
      finally
        FindClose(FindRec);
      end;
    for I := 0 to Names.Count - 1 do
    begin
      IsLog := (CompareText(Names[I], 'logs') = 0) or (Pos('EVENTS.LOG', Uppercase(Names[I])) = 1);
      if (IsLog and RemoveLogs) or (not IsLog and RemoveSettings) then
      begin
        if DirExists(AddBackslash(Dir) + Names[I]) then
          DelTree(AddBackslash(Dir) + Names[I], True, True, True)
        else
          DeleteFile(AddBackslash(Dir) + Names[I]);
      end;
    end;
  finally
    Names.Free;
  end;
  RemoveDir(Dir);
end;

// Настройки и журналы, отмеченные в окне удаления: папки %LOCALAPPDATA%\KillSwitchLauncher во всех профилях Windows.
procedure RemoveUserData;
var
  SIDs: TArrayOfString;
  Profile: String;
  I: Integer;
begin
  if not RemoveSettings and not RemoveLogs then
    Exit;
  CleanDataFolder(ExpandConstant('{localappdata}\KillSwitchLauncher'));
  if RegGetSubkeyNames(HKLM64, ProfileListKey, SIDs) then
    for I := 0 to GetArrayLength(SIDs) - 1 do
      if RegQueryStringValue(HKLM64, ProfileListKey + '\' + SIDs[I], 'ProfileImagePath', Profile) then
      begin
        ExpandEnvVar(Profile, 'SystemDrive');
        ExpandEnvVar(Profile, 'SystemRoot');
        CleanDataFolder(AddBackslash(Profile) + 'AppData\Local\KillSwitchLauncher');
      end;
end;

// Ключ лицензии удаляется при любом удалении: и без галочек, и в тихом режиме. В реестре — запись для всех
// пользователей, в профилях — файл license.dat (его пишет лаунчер, если ключ ввели в его окне без прав администратора).
procedure RemoveLicense;
var
  SIDs: TArrayOfString;
  Profile: String;
  I: Integer;
begin
  RegDeleteValue(HKLM64, LicenseRegKey, LicenseRegValue);
  RegDeleteValue(HKLM64, LicenseRegKey, 'LicenseKeyHash'); // след ранней сборки установщика, если остался
  RegDeleteKeyIfEmpty(HKLM64, LicenseRegKey);
  DeleteFile(ExpandConstant('{localappdata}\KillSwitchLauncher\license.dat'));
  if RegGetSubkeyNames(HKLM64, ProfileListKey, SIDs) then
    for I := 0 to GetArrayLength(SIDs) - 1 do
      if RegQueryStringValue(HKLM64, ProfileListKey + '\' + SIDs[I], 'ProfileImagePath', Profile) then
      begin
        ExpandEnvVar(Profile, 'SystemDrive');
        ExpandEnvVar(Profile, 'SystemRoot');
        DeleteFile(AddBackslash(Profile) + 'AppData\Local\KillSwitchLauncher\license.dat');
      end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  case CurUninstallStep of
    // Что удалить вместе с лаунчером. Без окон (/SILENT, /VERYSILENT) настройки и журналы остаются.
    usAppMutexCheck:
      if not UninstallSilent and not AskWhatToRemove then
        Abort;
    // Служба останавливается (ядро вместе с ней) и удаляется до удаления файлов. Фильтры Windows лаунчер
    // ставит только на время работы, поэтому после удаления в системе ничего не остаётся.
    usUninstall:
      begin
        Exec(ExpandConstant('{#CoreDir}\{#ServiceExe}'), '--uninstall', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
        StopService; // если служба не удалилась, файлы всё равно должны освободиться
      end;
    usPostUninstall:
      begin
        RemoveUserData;
        RemoveLicense; // ключ лицензии удаляется всегда
      end;
  end;
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
