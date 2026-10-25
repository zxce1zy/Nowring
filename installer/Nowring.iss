; Установщик Nowring (Inno Setup 6): https://jrsoftware.org/isinfo.php
; Сборка: installer\make-installer.bat или GitHub Actions (см. .github/workflows/build.yml)

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

[Setup]
AppId={{DC91EFF5-2F0C-412E-97D9-3C63A1660EE2}
AppName=Nowring
AppVersion={#MyAppVersion}
AppVerName=Nowring {#MyAppVersion}
AppPublisher=zxce1zy
AppPublisherURL=https://github.com/zxce1zy
AppSupportURL=https://github.com/zxce1zy/Nowring/issues
AppUpdatesURL=https://github.com/zxce1zy/Nowring/releases
AppCopyright=Copyright (c) 2026 zxce1zy

; Путь установки можно выбрать на странице мастера. По умолчанию ставится только для текущего
; пользователя (права администратора не нужны); в начале мастера можно выбрать «для всех».
DefaultDirName={autopf}\Nowring
DisableDirPage=no
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UsePreviousAppDir=yes

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041

OutputDir=..\dist
OutputBaseFilename=Nowring-Setup-{#MyAppVersion}
SetupIconFile=..\assets\icon.ico
UninstallDisplayIcon={app}\Nowring.exe
UninstallDisplayName=Nowring

; тёмная тема мастера появилась в Inno Setup 6.6; на более старых версиях будет обычная светлая
#if Ver >= EncodeVer(6, 6, 0)
WizardStyle=modern dark
#else
WizardStyle=modern
#endif
WizardImageFile=..\assets\wizard.png
WizardSmallImageFile=..\assets\wizard_small.png
Compression=lzma2/ultra64
SolidCompression=yes
SetupMutex=NowringSetupMutex
CloseApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
russian.AutoStartTask=Запускать вместе с Windows
english.AutoStartTask=Start with Windows
russian.LaunchApp=Запустить Nowring
english.LaunchApp=Launch Nowring
russian.RemoveData=Удалить также настройки и кэш обложек Nowring?
english.RemoveData=Also remove Nowring settings and cover cache?

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "{cm:AutoStartTask}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\out\Nowring.exe"; DestDir: "{app}"; Flags: ignoreversion
; библиотеки WPF, которые publish кладёт рядом с exe: без них программа из папки установки не запускается
Source: "..\out\*.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Nowring"; Filename: "{app}\Nowring.exe"
Name: "{autodesktop}\Nowring"; Filename: "{app}\Nowring.exe"; Tasks: desktopicon

[Registry]
; тот же ключ, что использует переключатель «Запускать вместе с Windows» в самой программе
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Nowring"; ValueData: """{app}\Nowring.exe"" --hidden"; Tasks: autostart; Flags: uninsdeletevalue

; если автозапуск когда-то отключали в Диспетчере задач, снимаем отметку, иначе он не сработает
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"; ValueType: none; ValueName: "Nowring"; Tasks: autostart; Flags: deletevalue

[Run]
; --show: после установки сразу открывается окно программы
Filename: "{app}\Nowring.exe"; Parameters: "--show"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM Nowring.exe"; Flags: runhidden; RunOnceId: "KillNowring"

[Code]
// Программа живёт в трее без окон, поэтому перед копированием файлов завершаем её.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Nowring.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Nowring');
    if (not UninstallSilent) and
       (MsgBox(CustomMessage('RemoveData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
    begin
      DelTree(ExpandConstant('{userappdata}\Nowring'), True, True, True);
      DelTree(ExpandConstant('{localappdata}\Nowring'), True, True, True);
    end;
  end;
end;
