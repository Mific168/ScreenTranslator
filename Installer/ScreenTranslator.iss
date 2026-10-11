; Установщик «Экранного переводчика» (Inno Setup 6, https://jrsoftware.org/isinfo.php).
; Сборка: Installer\build-installer.ps1 (сначала публикует single-file exe, затем компилирует этот скрипт).
;
; Что делает установщик:
;  • ставит приложение в %LocalAppData%\Programs\ScreenTranslator (права администратора не нужны);
;  • создаёт ярлык в меню «Пуск» (и по желанию на рабочем столе), регистрирует удаление;
;  • по желанию включает автозапуск вместе с Windows;
;  • после установки запускает приложение, которое само включает офлайн-перевод
;    и скачивает выбранные языковые модели (окно с прогрессом);
;  • удаление («Параметры → Приложения» или «Панель управления») закрывает приложение и удаляет всё:
;    программу, ярлыки, автозапуск, языковые модели, временные файлы и — по выбору — настройки и историю.

#define AppName "Экранный переводчик"
#define AppExe "ScreenTranslator.exe"
#define AppVersion "0.0.2"
#define AppPublisher "ScreenTranslator"

[Setup]
AppId={{6E4C1B7A-3F2D-4B8E-9A51-0C7D2E8F4A11}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\ScreenTranslator
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 версии 2004 и новее
MinVersion=10.0.19041
OutputDir=..\bin\installer
OutputBaseFilename=ScreenTranslatorSetup
SetupIconFile=..\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; Запущенное приложение закрывается автоматически перед обновлением и удалением (см. [Code])
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[CustomMessages]
ru.GroupShortcuts=Ярлыки и запуск:
ru.GroupLanguages=Языки для офлайн-перевода (скачиваются после установки, ~110 МБ на направление):

[Tasks]
Name: "autostart"; Description: "Запускать вместе с Windows"; GroupDescription: "{cm:GroupShortcuts}"
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "{cm:GroupShortcuts}"; Flags: unchecked

Name: "lang_en"; Description: "Английский ↔ русский (~220 МБ)"; GroupDescription: "{cm:GroupLanguages}"
Name: "lang_uk"; Description: "Украинский ↔ русский (~220 МБ)"; GroupDescription: "{cm:GroupLanguages}"; Flags: unchecked
Name: "lang_de"; Description: "Немецкий ↔ русский, через английский (+~220 МБ)"; GroupDescription: "{cm:GroupLanguages}"; Flags: unchecked
Name: "lang_fr"; Description: "Французский ↔ русский (~220 МБ)"; GroupDescription: "{cm:GroupLanguages}"; Flags: unchecked
Name: "lang_es"; Description: "Испанский ↔ русский (~220 МБ)"; GroupDescription: "{cm:GroupLanguages}"; Flags: unchecked
Name: "lang_it"; Description: "Итальянский ↔ русский, через английский (+~220 МБ)"; GroupDescription: "{cm:GroupLanguages}"; Flags: unchecked
Name: "lang_zh"; Description: "Китайский ↔ русский, через английский (+~220 МБ)"; GroupDescription: "{cm:GroupLanguages}"; Flags: unchecked
Name: "lang_ja"; Description: "Японский → русский, через английский (+~110 МБ)"; GroupDescription: "{cm:GroupLanguages}"; Flags: unchecked
Name: "lang_ko"; Description: "Корейский → русский, через английский (+~110 МБ)"; GroupDescription: "{cm:GroupLanguages}"; Flags: unchecked
Name: "lang_pl"; Description: "Польский → русский, через английский (+~110 МБ)"; GroupDescription: "{cm:GroupLanguages}"; Flags: unchecked
Name: "lang_tr"; Description: "Турецкий → русский, через английский (+~110 МБ)"; GroupDescription: "{cm:GroupLanguages}"; Flags: unchecked
Name: "lang_nllb"; Description: "Качественная модель NLLB-200 для всех языков (~870 МБ; медленнее, до 3 ГБ памяти при переводе)"; GroupDescription: "{cm:GroupLanguages}"; Flags: unchecked

[Files]
Source: "..\bin\publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Автозапуск (то же значение приложение пишет само, когда автозапуск включают в настройках)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "ScreenTranslator"; \
    ValueData: """{app}\{#AppExe}"" --autostart"; Tasks: autostart; Flags: uninsdeletevalue
; При удалении убрать автозапуск, даже если его включили позже в настройках приложения
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "ScreenTranslator"; \
    Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Parameters: "--install-models {code:GetModels} --native ru"; \
    Description: "Запустить «{#AppName}» и скачать выбранные языки"; Flags: postinstall nowait skipifsilent

[InstallDelete]
; Старые распакованные копии библиотек (single-file exe распаковывает их в %TEMP%\.net при запуске;
; после каждого обновления остаётся ещё одна копия ~20 МБ)
Type: filesandordirs; Name: "{%TEMP}\.net\ScreenTranslator"

[UninstallDelete]
; Языковые модели (могут занимать сотни мегабайт)
Type: filesandordirs; Name: "{localappdata}\ScreenTranslator"
; Распакованные библиотеки приложения
Type: filesandordirs; Name: "{%TEMP}\.net\ScreenTranslator"
; Всё, что могло появиться в папке программы
Type: filesandordirs; Name: "{app}"
; Настройки, история и ключи API (%AppData%\ScreenTranslator) удаляются после вопроса — см. [Code]

[Code]
var
  Models: String;

procedure AddModel(const Id: String);
begin
  if Pos(',' + Id + ',', ',' + Models + ',') = 0 then
  begin
    if Models <> '' then Models := Models + ',';
    Models := Models + Id;
  end;
end;

{ Языки, которые переводятся через английский, требуют пары «английский ↔ русский». }
procedure AddPivot;
begin
  AddModel('en-ru');
  AddModel('ru-en');
end;

function GetModels(Param: String): String;
begin
  Models := '';
  if WizardIsTaskSelected('lang_en') then AddPivot;
  if WizardIsTaskSelected('lang_uk') then begin AddModel('uk-ru'); AddModel('ru-uk'); end;
  if WizardIsTaskSelected('lang_de') then begin AddModel('de-en'); AddModel('en-de'); AddPivot; end;
  if WizardIsTaskSelected('lang_fr') then begin AddModel('fr-ru'); AddModel('ru-fr'); end;
  if WizardIsTaskSelected('lang_es') then begin AddModel('es-ru'); AddModel('ru-es'); end;
  if WizardIsTaskSelected('lang_it') then begin AddModel('it-en'); AddModel('en-it'); AddPivot; end;
  if WizardIsTaskSelected('lang_zh') then begin AddModel('zh-en'); AddModel('en-zh'); AddPivot; end;
  if WizardIsTaskSelected('lang_ja') then begin AddModel('ja-en'); AddPivot; end;
  if WizardIsTaskSelected('lang_ko') then begin AddModel('ko-en'); AddPivot; end;
  if WizardIsTaskSelected('lang_pl') then begin AddModel('pl-en'); AddPivot; end;
  if WizardIsTaskSelected('lang_tr') then begin AddModel('tr-en'); AddPivot; end;
  if WizardIsTaskSelected('lang_nllb') then AddModel('nllb-200');
  if Models = '' then Models := 'none';
  Result := Models;
end;

{ ---------- Закрытие приложения ---------- }

procedure CloseRunningApp;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if ResultCode = 0 then Sleep(700); { дать Windows освободить exe }
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CloseRunningApp;
  Result := '';
end;

{ ---------- Полное удаление ---------- }

var
  DeleteUserData: Boolean;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    CloseRunningApp;
    { При тихом удалении (/SILENT) данные пользователя сохраняются }
    DeleteUserData := SuppressibleMsgBox(
      'Удалить также настройки, историю переводов и сохранённые ключи API?' + #13#10#13#10 +
      'Нажмите «Нет», если собираетесь установить приложение снова и хотите сохранить настройки.',
      mbConfirmation, MB_YESNO, IDNO) = IDYES;
  end;

  if (CurUninstallStep = usPostUninstall) and DeleteUserData then
    DelTree(ExpandConstant('{userappdata}\ScreenTranslator'), True, True, True);
end;
