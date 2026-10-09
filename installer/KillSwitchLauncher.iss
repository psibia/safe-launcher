; Установщик «Безопасного лаунчера» для Inno Setup 6.
;
; Собирать удобнее всего через build-installer.cmd (лежит рядом): он публикует приложение
; в папку publish и затем компилирует этот скрипт. Готовый установщик появится в папке Output.
;
; Приложение публикуется как self-contained: на ПК коллег не нужно ставить .NET 8.

#define MyAppName "Безопасный лаунчер"
#define MyAppExeName "KillSwitchLauncher.exe"
#define MyPublishDir AddBackslash(SourcePath) + "publish"

#if !FileExists(MyPublishDir + "\" + MyAppExeName)
  #error "Нет publish\KillSwitchLauncher.exe. Сначала запустите build-installer.cmd (или dotnet publish, см. README)."
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

; По умолчанию ставится только для текущего пользователя и без прав администратора.
; Установщик предложит выбор: «для всех пользователей» потребует прав администратора.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

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

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
; Обновление из самого лаунчера: он запускает установщик с /SILENT /autoupdate=1 и закрывается,
; а после установки новая версия запускается здесь (от имени обычного пользователя, даже если установка шла с правами администратора).
Filename: "{app}\{#MyAppExeName}"; Flags: nowait runasoriginaluser skipifnotsilent; Check: IsAutoUpdate

; Настройки пользователя (%LOCALAPPDATA%\KillSwitchLauncher\settings.json) при удалении намеренно
; не трогаем: при переустановке список приложений и правила VPN сохранятся.

[Code]
// Установщик запущен лаунчером для обновления (параметр /autoupdate=1).
function IsAutoUpdate: Boolean;
begin
  Result := ExpandConstant('{param:autoupdate|0}') = '1';
end;
