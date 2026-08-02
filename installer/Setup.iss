; Inno Setup script — собирает установщик Tweak Firmware из результата
; "dotnet publish" (self-contained, single-file, win-x64), лежащего в ../publish.
;
; Версия передаётся снаружи через /DMyAppVersion=X.Y.Z (см. .github/workflows/release.yml);
; если собирать локально без этого параметра — используется запасное значение ниже.
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

#define MyAppName "Tweak Firmware"
#define MyAppPublisher "Alxmak"
#define MyAppURL "https://github.com/Alxmak/BinConverter"
#define MyAppExeName "TweakFirmware.exe"

[Setup]
; Фиксированный AppId — нужен, чтобы Windows считала разные версии одной и той же
; программой (корректное обновление/удаление вместо установки "рядом").
AppId={{4E6C6D3E-6B0A-4E8B-9C6F-4E4E1E6E6C3E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\installer-output
OutputBaseFilename=TweakFirmwareSetup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
UninstallDisplayIcon={app}\{#MyAppExeName}
; Программа не подписана цифровой подписью — это ожидаемо для небольшой
; программы для личного использования (см. README/"О программе").

; Установка в профиль текущего пользователя, без прав администратора — {autopf}
; и {group}/{autodesktop} выше сами превращаются в пользовательские эквиваленты
; (%LocalAppData%\Programs, Меню Пуск/Рабочий стол текущего пользователя), когда
; PrivilegesRequired=lowest. Без этого при каждом автообновлении показывался бы
; запрос UAC — а обновление должно проходить полностью без действий пользователя.
PrivilegesRequired=lowest
; На случай, если exe всё же остался запущен к моменту копирования файлов
; (авто-обновление само закрывает программу перед запуском установщика, но это
; подстраховка) — Setup сам закроет процесс, а не покажет ошибку "файл занят".
; Обратно не перезапускаем — этим занимается секция [Run] ниже.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Всё содержимое папки публикации (self-contained single-file exe) — кроме .pdb,
; который нужен только для отладки и не нужен конечному пользователю.
Source: "..\publish\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Без skipifsilent: при автообновлении установка проходит в тихом режиме (/VERYSILENT),
; и программу всё равно нужно перезапустить самостоятельно — интерактивного мастера
; с чекбоксом "Запустить программу" в этом режиме просто не будет.
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall
