; GigaCleaner — установщик
; Собирает single-file exe в Program Files, создаёт ярлыки (рабочий стол + меню "Пуск"),
; регистрирует удаление. Данные приложения хранятся в %APPDATA%\GigaCleaner отдельно.

[Setup]
AppId={{8E6A2B1C-4D3F-9A2B-8C1D-6E5F4A3B2C1E}
AppName=GigaCleaner
AppVersion=1.0.0
AppPublisher=GigaCleaner
DefaultDirName={autopf}\GigaCleaner
DefaultGroupName=GigaCleaner
DisableProgramGroupPage=yes
OutputDir=installer
OutputBaseFilename=GigaCleaner-Setup-1.0.0
SetupIconFile=Assets\app.ico
UninstallDisplayIcon={app}\GigaCleaner.exe
UninstallIconFile=Assets\app.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern dark
WizardImageFile=Installer\res\wizard-bg.png
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=10.0.17763
UninstallDisplayName=GigaCleaner
CloseApplications=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Дополнительно:"; Flags: checkedonce

[Files]
Source: "artifacts\PortableSingleFile\GigaCleaner.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\GigaCleaner"; Filename: "{app}\GigaCleaner.exe"
Name: "{autodesktop}\GigaCleaner"; Filename: "{app}\GigaCleaner.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\GigaCleaner.exe"; Description: "Запустить GigaCleaner"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: dirifempty; Name: "{app}"

[Code]
var
  WasAppRunning: Boolean;

function InitializeSetup(): Boolean;
var
  Path: string;
  FreeBytes, TotalBytes: Int64;
begin
  Result := True;
  Path := ExpandConstant('{autopf}');
  if GetSpaceOnDisk64(Path, FreeBytes, TotalBytes) then
  begin
    if FreeBytes < 250 * 1024 * 1024 then
    begin
      Result := False;
      MsgBox('Недостаточно места на диске.' + #13#10 +
        'Для установки GigaCleaner требуется не менее 250 МБ свободного места.', mbError, MB_OK);
    end;
  end;
end;

procedure DarkPanels(Control: TWinControl);
var
  I: Integer;
  C: TControl;
begin
  if Control is TPanel then
  begin
    TPanel(Control).Color := $1F1F23;
    TPanel(Control).ParentColor := False;
  end;
  for I := 0 to Control.ControlCount - 1 do
  begin
    C := Control.Controls[I];
    if C is TPanel then
      DarkPanels(TPanel(C));
  end;
end;

procedure DarkTexts(Control: TWinControl);
var
  I: Integer;
  C: TControl;
begin
  if Control is TLabel then
    TLabel(Control).Font.Color := $FAFAFA
  else if Control is TNewStaticText then
    TNewStaticText(Control).Font.Color := $FAFAFA
  else if Control is TNewCheckBox then
    TNewCheckBox(Control).Font.Color := $FAFAFA
  else if Control is TNewRadioButton then
    TNewRadioButton(Control).Font.Color := $FAFAFA;

  for I := 0 to Control.ControlCount - 1 do
  begin
    C := Control.Controls[I];
    if C is TWinControl then
      DarkTexts(TWinControl(C));
  end;
end;

const
  AppExeName = 'GigaCleaner.exe';
  GCAppKey = 'Software\GigaCleaner';

/// Вежливо закрывает работающий экземпляр приложения (без /F — штатное закрытие окна).
procedure CloseRunningApp();
var
  ResultCode: Integer;
begin
  if WasAppRunning then
    Exec('taskkill.exe', '/IM ' + AppExeName, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  S: string;
begin
  WasAppRunning := False;
  if FileExists(ExpandConstant('{app}\') + AppExeName)
     and (FindWindowByWindowName('GigaCleaner') <> 0) then
    WasAppRunning := True;
  if WasAppRunning then
    RegWriteStringValue(HKLM, GCAppKey, 'WasRunning', '1')
  else
    RegDeleteValue(HKLM, GCAppKey, 'WasRunning');
  CloseRunningApp();
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  ShouldRestart: string;
begin
  if CurStep = ssPostInstall then
  begin
    ShouldRestart := '';
    RegQueryStringValue(HKLM, GCAppKey, 'WasRunning', ShouldRestart);
    RegDeleteValue(HKLM, GCAppKey, 'WasRunning');
    if ShouldRestart = '1' then
      Exec(ExpandConstant('{app}\') + AppExeName, '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
  end;
end;

procedure InitializeWizard;
begin
  WizardForm.Color := $1F1F23;

  DarkPanels(WizardForm);
  DarkTexts(WizardForm);

  WizardForm.PageNameLabel.Font.Color := $2563EB;

  WizardForm.FinishedHeadingLabel.Caption := 'Установка GigaCleaner завершена';
  WizardForm.FinishedLabel.Caption := 'GigaCleaner 1.0.0 установлен. Программа готова к работе — просто нажмите «Завершить».';
  WizardForm.PageDescriptionLabel.Font.Color := $A1A1AA;
  WizardForm.FinishedHeadingLabel.Font.Color := $FAFAFA;
  WizardForm.FinishedLabel.Font.Color := $A1A1AA;
end;

var
  DeleteSettings: Boolean;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  DeleteSettings := False;
  if FileExists(ExpandConstant('{userappdata}\GigaCleaner\settings.json')) then
    DeleteSettings := (MsgBox('Удалить вместе с программой и настройки GigaCleaner?' + #13#10 +
      '(файлы %AppData%\GigaCleaner: профиль, расписание, тема, список «сохранения входа»)', mbConfirmation, MB_YESNO) = IDYES);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  case CurUninstallStep of
    usPostUninstall:
      if DeleteSettings then
        DelTree(ExpandConstant('{userappdata}\GigaCleaner'), True, True, False);
  end;
end;