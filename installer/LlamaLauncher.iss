; LlamaLauncher 安装包脚本（Inno Setup 6+）
; 构建：先运行 scripts/publish-portable.ps1，再执行 scripts/build-installer.ps1
; 设计要点：
;  - 默认按用户安装（不需要管理员权限），安装目录可选；
;  - 卸载默认保留用户数据（%AppData%\LlamaLauncher：数据库、聊天、日志、已安装 llama.cpp 版本）；
;  - 不包含任何 llama.cpp 二进制与 GGUF 模型；不修改防火墙/驱动/环境变量。

#define AppName "LlamaLauncher"
#define AppVersion "0.9.0"
#define AppPublisher "LlamaLauncher contributors"
; 便携版来源目录：默认取标准命名；构建脚本会通过 /DPortableSource=<绝对路径> 传入实际目录
#ifndef PortableSource
  #define PortableSource "..\artifacts\portable\LlamaLauncher-" + AppVersion + "-win-x64"
#endif

[Setup]
AppId={{7E0F1B9C-3A21-4D3B-9D01-LLAMALAUNCHER01}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\artifacts\installer
OutputBaseFilename=LlamaLauncher-Setup-{#AppVersion}-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\LlamaLauncher.App\Assets\LlamaLauncher.ico
UninstallDisplayIcon={app}\LlamaLauncher.exe
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: unchecked

[Files]
Source: "{#PortableSource}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\LlamaLauncher.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\LlamaLauncher.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\LlamaLauncher.exe"; Description: "立即运行 {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: dirifempty; Name: "{app}"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: string;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{userappdata}\LlamaLauncher');
    if DirExists(DataDir) then
      MsgBox('用户数据已保留在：' + DataDir + #13#10 +
             '其中包含数据库、聊天历史、日志与已安装的 llama.cpp 版本。' + #13#10 +
             '如需彻底清理，请手动删除该目录。', mbInformation, MB_OK);
  end;
end;
