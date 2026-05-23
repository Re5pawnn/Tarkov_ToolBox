#define MyAppName "TarkovMapLocator"
#define MyAppVersion "1.2.2"
#define MyAppPublisher "Re5pawnn"
#define MyAppURL "https://github.com/Re5pawnn/Tarkov_webmap"
#define MyAppReleasesURL "https://github.com/Re5pawnn/Tarkov_webmap/releases/latest"
#define MyAppLauncher "TarkovMapLocator.exe"
#define MyAppBuildDir "..\src\TarkovMapLocator.App\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\publish"

[Setup]
AppId={{A3E3D2D1-77EF-4D33-8A31-B8E77EA1A4F2}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppReleasesURL}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DisableDirPage=no
AlwaysShowDirOnReadyPage=yes
UsePreviousAppDir=yes
DisableProgramGroupPage=yes
OutputDir={#SourcePath}\dist
OutputBaseFilename=TarkovMapLocator
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
UninstallDisplayIcon={app}\{#MyAppLauncher}

[Languages]
Name: "chinesesimplified"; MessagesFile: "{#SourcePath}\ChineseSimplified.isl"

[Tasks]
Name: "startmenuicon"; Description: "创建开始菜单快捷方式"; GroupDescription: "选择要创建的快捷方式："; Flags: checkedonce
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "选择要创建的快捷方式："; Flags: unchecked

[Files]
Source: "{#SourcePath}\{#MyAppBuildDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
Type: files; Name: "{app}\app.js"
Type: files; Name: "{app}\index.html"
Type: files; Name: "{app}\styles.css"
Type: files; Name: "{app}\start_tool.bat"
Type: files; Name: "{app}\start_tool_hidden.vbs"
Type: files; Name: "{app}\WebView2Loader.dll"
Type: files; Name: "{app}\Microsoft.Web.WebView2*.dll"
Type: filesandordirs; Name: "{app}\TarkovMapLocator.exe.WebView2"

[Icons]
Name: "{autoprograms}\{#MyAppName}\{#MyAppName}"; Filename: "{app}\{#MyAppLauncher}"; WorkingDir: "{app}"; Tasks: startmenuicon
Name: "{autoprograms}\{#MyAppName}\检查更新"; Filename: "{#MyAppReleasesURL}"; Tasks: startmenuicon
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppLauncher}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppLauncher}"; Description: "立即启动工具"; Flags: nowait postinstall skipifsilent shellexec
