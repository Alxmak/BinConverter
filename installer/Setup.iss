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
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
