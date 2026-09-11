#define MyAppName "塔科夫工具箱"
#ifndef MyAppVersion
#define MyAppVersion "31.27.63"
#endif
#define MyAppPublisher "TarkovMapLocator"
#define MyAppExeName "TarkovToolbox.exe"
#ifndef MyAppSource
#define MyAppSource "..\..\TarkovMapLocatorDesktop"
#endif

[Setup]
SetupIconFile=assets\branding\app-icon.ico
AppId={{5EECFD20-AC95-4EB0-9F46-70B1D84B1231}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
DefaultDirName={autopf}\Tarkov Toolbox
DefaultGroupName={#MyAppName}
DisableDirPage=no
DisableProgramGroupPage=yes
AllowNoIcons=yes
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=commandline dialog
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir=..\..\release
OutputBaseFilename=TarkovToolbox-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ShowComponentSizes=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
CloseApplicationsFilter={#MyAppExeName}
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: ".\ChineseSimplified.isl"

[Types]
Name: "full"; Description: "完整安装"
Name: "compact"; Description: "精简安装（仅核心程序）"
Name: "custom"; Description: "自定义安装"; Flags: iscustom

[Components]
Name: "core"; Description: "核心程序"; Types: full compact custom; Flags: fixed
Name: "market"; Description: "市场"; Types: full
Name: "taskitems"; Description: "任务物品清单"; Types: full
Name: "memo"; Description: "备忘录"; Types: full
Name: "tasktracking"; Description: "任务追踪"; Types: full
Name: "ingameprice"; Description: "战局查价"; Types: full
Name: "screenfilter"; Description: "屏幕调色"; Types: full
Name: "teamsync"; Description: "队友位置共享"; Types: full
Name: "mobilemap"; Description: "手机地图"; Types: full
Name: "utilities"; Description: "小工具"; Types: full

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式选项："; Flags: unchecked

[InstallDelete]
Type: filesandordirs; Name: "{app}\Modules"
Type: filesandordirs; Name: "{app}\assets\weapon-build"
Type: filesandordirs; Name: "{app}\bootstrap-data"
Type: files; Name: "{app}\task-data\task-game-id-map.json"
Type: files; Name: "{app}\task-data\task-tracking-catalog.json"

[Files]
Source: "{#MyAppSource}\*"; DestDir: "{app}"; Excludes: "\Modules\*,\bootstrap-data\*,\task-data\task-game-id-map.json,\task-data\task-tracking-catalog.json"; Components: core; Flags: ignoreversion recursesubdirs
Source: "{#MyAppSource}\Modules\Market\*"; DestDir: "{app}\Modules\Market"; Components: market; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#MyAppSource}\Modules\TaskItems\*"; DestDir: "{app}\Modules\TaskItems"; Components: taskitems; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#MyAppSource}\Modules\Memo\*"; DestDir: "{app}\Modules\Memo"; Components: memo; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#MyAppSource}\Modules\TaskTracking\*"; DestDir: "{app}\Modules\TaskTracking"; Components: tasktracking; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#MyAppSource}\Modules\InGamePrice\*"; DestDir: "{app}\Modules\InGamePrice"; Components: ingameprice; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#MyAppSource}\Modules\ScreenFilter\*"; DestDir: "{app}\Modules\ScreenFilter"; Components: screenfilter; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#MyAppSource}\Modules\TeamSync\*"; DestDir: "{app}\Modules\TeamSync"; Components: teamsync; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#MyAppSource}\Modules\MobileMap\*"; DestDir: "{app}\Modules\MobileMap"; Components: mobilemap; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#MyAppSource}\Modules\Utilities\*"; DestDir: "{app}\Modules\Utilities"; Components: utilities; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#MyAppSource}\bootstrap-data\seed-id.txt"; DestDir: "{app}\bootstrap-data"; Components: market taskitems memo ingameprice utilities; Flags: ignoreversion
Source: "{#MyAppSource}\bootstrap-data\market-cache.json"; DestDir: "{app}\bootstrap-data"; Components: market taskitems memo ingameprice utilities; Flags: ignoreversion
Source: "{#MyAppSource}\bootstrap-data\item-tracker-cache.json"; DestDir: "{app}\bootstrap-data"; Components: taskitems; Flags: ignoreversion
Source: "{#MyAppSource}\bootstrap-data\hideout-profit-cache.json"; DestDir: "{app}\bootstrap-data"; Components: utilities; Flags: ignoreversion
Source: ".\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Components: core; Flags: ignoreversion

[Icons]
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppName}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent
