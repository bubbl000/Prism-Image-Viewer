#define MyAppName "棱镜图片浏览器"
#define MyAppNameEn "Prism Image Viewer"
#define MyAppVersion "2.5"
#define MyAppPublisher "棱镜"
#define MyAppExeName "ImageViewer.exe"
#define MyAppSrcDir "bin\Release\net8.0-windows\win-x64\publish"

[Setup]
AppId={{A3B7C912-4D6E-4F8A-B102-2E5D7F9C3A84}
AppName={cm:AppFullName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppVerName={cm:AppFullName} {#MyAppVersion}
DefaultDirName={autopf}\{cm:AppFolder}
DefaultGroupName={cm:AppFullName}
OutputDir=installer_output
OutputBaseFilename=PrismImageViewer_Setup_{#MyAppVersion}
SetupIconFile=Assets\icon.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={cm:AppFullName}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ShowLanguageDialog=yes
VersionInfoDescription=棱镜图片浏览器 安装程序
VersionInfoProductName=棱镜图片浏览器

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
; 应用名称
chinesesimplified.AppFullName=棱镜图片浏览器
english.AppFullName=Prism Image Viewer

; 安装目录名
chinesesimplified.AppFolder=棱镜图片浏览器
english.AppFolder=Prism Image Viewer

; 桌面快捷方式
chinesesimplified.CreateDesktopIcon=创建桌面快捷方式
english.CreateDesktopIcon=Create a desktop shortcut

; 运行应用
chinesesimplified.LaunchApp=立即运行棱镜图片浏览器
english.LaunchApp=Launch Prism Image Viewer

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#MyAppSrcDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{cm:AppFullName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{cm:AppFullName}}"; Filename: "{uninstallexe}"
Name: "{commondesktop}\{cm:AppFullName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Classes\ImageViewer.AssocFile"; ValueType: string; ValueData: "{cm:AppFullName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\ImageViewer.AssocFile\shell\open\command"; ValueType: string; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Flags: uninsdeletekey

[UninstallDelete]
Type: filesandordirs; Name: "{userappdata}\ImageViewer"
Type: filesandordirs; Name: "{%TEMP}\ImageViewerThumbCache"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent
