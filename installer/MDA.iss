; MDA v1: source preparation only. The complete formal editor is the only normal entry point.
; Derived structurally from Inno Setup's official Examples/Components.iss.
; https://github.com/jrsoftware/issrc/blob/main/Examples/Components.iss
; Inno Setup compiler license: https://github.com/jrsoftware/issrc/blob/main/license.txt
#ifndef StageDir
  #error Supply StageDir using scripts/package-editor.ps1 after release acceptance.
#endif
#ifndef MdaVersion
  #error Supply MdaVersion.
#endif
#ifndef InstallerOutput
  #error Supply InstallerOutput under deliverables/Installer.
#endif
#if !FileExists(StageDir + "\package-manifest.json") || !FileExists(StageDir + "\release-acceptance.json")
  #error Staging manifest and completed release evidence are required.
#endif
#if FileExists(StageDir + "\tools\classic\classic-runtime.json")
  #define HasClassic
  #define EditorTypes "full custom"
#else
  #define EditorTypes "full"
#endif

[Setup]
; Keep this ID and install scope stable across upgrades. Do not append a version to AppId.
AppId=MDA.MikuDanceAdvance
AppName=MDA (MikuDance Advance)
AppVersion={#MdaVersion}
; Fresh installs start on D:. UsePreviousAppDir below preserves an existing chosen location.
DefaultDirName=D:\MDA
; Keep the destination editor and Browse button available, including during upgrades.
DisableDirPage=no
DefaultGroupName=MDA
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0
UninstallDisplayIcon={app}\Mmd.Modern.App.exe
SetupIconFile=..\src\Mmd.Modern.App\Assets\Branding\mda.ico
OutputDir={#InstallerOutput}
OutputBaseFilename=MDA-{#MdaVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
UsePreviousAppDir=yes
UsePreviousTasks=yes
UsePreviousLanguage=yes
UsePreviousSetupType=yes
ChangesAssociations=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
; Prefer the compiler translation when available. The fallback is from jrsoftware/issrc is-6_7_4.
#if FileExists(AddBackslash(CompilerPath) + "Languages\ChineseSimplified.isl")
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
#else
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"
#endif
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Types]
Name: "full"; Description: "MDA"
#ifdef HasClassic
Name: "custom"; Description: "{cm:CustomInstallType}"; Flags: iscustom
#endif

[Components]
Name: "editor"; Description: "{cm:SharedComponent}"; Types: {#EditorTypes}; Flags: fixed
Name: "d3d12"; Description: "{cm:ModernComponent}"; Types: {#EditorTypes}; Flags: fixed
#ifdef HasClassic
Name: "classic"; Description: "{cm:ClassicComponent}"
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

Name: "associatepma"; Description: "{cm:associatepma}"; GroupDescription: "{cm:FileAssociations}"
Name: "associatepmc"; Description: "{cm:associatepmc}"; GroupDescription: "{cm:FileAssociations}"
Name: "associatepmm"; Description: "{cm:associatepmm}"; GroupDescription: "{cm:FileAssociations}"; Flags: unchecked
Name: "associatemodels"; Description: "{cm:associatemodels}"; GroupDescription: "{cm:FileAssociations}"; Flags: unchecked
Name: "associatemotions"; Description: "{cm:associatemotions}"; GroupDescription: "{cm:FileAssociations}"; Flags: unchecked
Name: "associateposes"; Description: "{cm:associateposes}"; GroupDescription: "{cm:FileAssociations}"; Flags: unchecked

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Excludes: "Mmd.Modern.Rendering.D3D12.dll,Mmd.Modern.ViewOutput.D3D12.dll,mmd_mikktspace.dll,tools\dxc,tools\dxc\*,tools\upscaling,tools\upscaling\*,tools\classic,tools\classic\*"; Components: editor; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StageDir}\Mmd.Modern.Rendering.D3D12.dll"; DestDir: "{app}"; Components: d3d12; Flags: ignoreversion
Source: "{#StageDir}\Mmd.Modern.ViewOutput.D3D12.dll"; DestDir: "{app}"; Components: d3d12; Flags: ignoreversion
Source: "{#StageDir}\mmd_mikktspace.dll"; DestDir: "{app}"; Components: d3d12; Flags: ignoreversion
Source: "{#StageDir}\tools\dxc\*"; DestDir: "{app}\tools\dxc"; Components: d3d12; Flags: ignoreversion recursesubdirs createallsubdirs
#if DirExists(StageDir + "\tools\upscaling")
Source: "{#StageDir}\tools\upscaling\*"; DestDir: "{app}\tools\upscaling"; Components: d3d12; Flags: ignoreversion recursesubdirs createallsubdirs
#endif
#ifdef HasClassic
Source: "{#StageDir}\tools\classic\*"; DestDir: "{app}\tools\classic"; Components: classic; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StageDir}\tools\classic\licenses\Microsoft.DXSDK.D3DX\LICENSE.txt"; DestName: "mda-classic-microsoft-license.txt"; Flags: dontcopy
#endif

[Registry]
; Inno official FAQ: https://jrsoftware.org/isfaq.php#creatingfileassociations
; Only app-owned ProgIDs/OpenWith values are removed. Existing Windows UserChoice is untouched.
; Register capabilities so Windows Default Apps can offer the selected file types.
; https://learn.microsoft.com/windows/apps/develop/launch/launch-default-apps-settings
Root: HKCU; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "MDA.MikuDanceAdvance"; ValueData: "Software\MDA\MikuDanceAdvance\Capabilities"; Flags: uninsdeletevalue; Check: HasFileAssociationTasks
Root: HKCU; Subkey: "Software\MDA\MikuDanceAdvance\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "MDA.MikuDanceAdvance"; Flags: uninsdeletekey; Check: HasFileAssociationTasks
Root: HKCU; Subkey: "Software\MDA\MikuDanceAdvance\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "{cm:AssociationDescription}"; Check: HasFileAssociationTasks
Root: HKCU; Subkey: "Software\MDA\MikuDanceAdvance\Capabilities"; ValueType: string; ValueName: "ApplicationIcon"; ValueData: """{app}\Mmd.Modern.App.exe"",0"; Check: HasFileAssociationTasks
Root: HKCU; Subkey: "Software\MDA\MikuDanceAdvance\Capabilities\FileAssociations"; ValueType: string; ValueName: ".pma"; ValueData: "MDA.AdvanceProject"; Flags: uninsdeletevalue; Tasks: associatepma
Root: HKCU; Subkey: "Software\MDA\MikuDanceAdvance\Capabilities\FileAssociations"; ValueType: string; ValueName: ".pmc"; ValueData: "MDA.ClassicProject"; Flags: uninsdeletevalue; Tasks: associatepmc
Root: HKCU; Subkey: "Software\MDA\MikuDanceAdvance\Capabilities\FileAssociations"; ValueType: string; ValueName: ".pmm"; ValueData: "MDA.MmdProject"; Flags: uninsdeletevalue; Tasks: associatepmm
Root: HKCU; Subkey: "Software\MDA\MikuDanceAdvance\Capabilities\FileAssociations"; ValueType: string; ValueName: ".pmx"; ValueData: "MDA.PmxModel"; Flags: uninsdeletevalue; Tasks: associatemodels
Root: HKCU; Subkey: "Software\MDA\MikuDanceAdvance\Capabilities\FileAssociations"; ValueType: string; ValueName: ".pmd"; ValueData: "MDA.PmdModel"; Flags: uninsdeletevalue; Tasks: associatemodels
Root: HKCU; Subkey: "Software\MDA\MikuDanceAdvance\Capabilities\FileAssociations"; ValueType: string; ValueName: ".vmd"; ValueData: "MDA.VmdMotion"; Flags: uninsdeletevalue; Tasks: associatemotions
Root: HKCU; Subkey: "Software\MDA\MikuDanceAdvance\Capabilities\FileAssociations"; ValueType: string; ValueName: ".vpd"; ValueData: "MDA.VpdPose"; Flags: uninsdeletevalue; Tasks: associateposes
Root: HKCU; Subkey: "Software\Classes\Applications\Mmd.Modern.App.exe"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "MDA (MikuDance Advance)"; Flags: uninsdeletevalue; Check: HasFileAssociationTasks
Root: HKCU; Subkey: "Software\Classes\Applications\Mmd.Modern.App.exe\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Mmd.Modern.App.exe"" --open ""%1"""; Flags: uninsdeletevalue uninsdeletekeyifempty; Check: HasFileAssociationTasks
Root: HKCU; Subkey: "Software\Classes\.pma\OpenWithProgids"; ValueType: string; ValueName: "MDA.AdvanceProject"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associatepma
Root: HKCU; Subkey: "Software\Classes\.pma"; ValueType: string; ValueName: ""; ValueData: "MDA.AdvanceProject"; Tasks: associatepma; Check: CanClaimUnassignedExtension('.pma')
Root: HKCU; Subkey: "Software\Classes\MDA.AdvanceProject"; ValueType: string; ValueName: ""; ValueData: "MDA Advance project"; Flags: uninsdeletekey; Tasks: associatepma
Root: HKCU; Subkey: "Software\Classes\MDA.AdvanceProject\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\Mmd.Modern.App.exe"",0"; Tasks: associatepma
Root: HKCU; Subkey: "Software\Classes\MDA.AdvanceProject\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Mmd.Modern.App.exe"" --open ""%1"""; Tasks: associatepma
Root: HKCU; Subkey: "Software\Classes\Applications\Mmd.Modern.App.exe\SupportedTypes"; ValueType: string; ValueName: ".pma"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associatepma
Root: HKCU; Subkey: "Software\Classes\.pmc\OpenWithProgids"; ValueType: string; ValueName: "MDA.ClassicProject"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associatepmc
Root: HKCU; Subkey: "Software\Classes\.pmc"; ValueType: string; ValueName: ""; ValueData: "MDA.ClassicProject"; Tasks: associatepmc; Check: CanClaimUnassignedExtension('.pmc')
Root: HKCU; Subkey: "Software\Classes\MDA.ClassicProject"; ValueType: string; ValueName: ""; ValueData: "MDA Classic project"; Flags: uninsdeletekey; Tasks: associatepmc
Root: HKCU; Subkey: "Software\Classes\MDA.ClassicProject\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\Mmd.Modern.App.exe"",0"; Tasks: associatepmc
Root: HKCU; Subkey: "Software\Classes\MDA.ClassicProject\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Mmd.Modern.App.exe"" --open ""%1"""; Tasks: associatepmc
Root: HKCU; Subkey: "Software\Classes\Applications\Mmd.Modern.App.exe\SupportedTypes"; ValueType: string; ValueName: ".pmc"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associatepmc
Root: HKCU; Subkey: "Software\Classes\.pmm\OpenWithProgids"; ValueType: string; ValueName: "MDA.MmdProject"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associatepmm
Root: HKCU; Subkey: "Software\Classes\.pmm"; ValueType: string; ValueName: ""; ValueData: "MDA.MmdProject"; Tasks: associatepmm; Check: CanClaimUnassignedExtension('.pmm')
Root: HKCU; Subkey: "Software\Classes\MDA.MmdProject"; ValueType: string; ValueName: ""; ValueData: "MMD project"; Flags: uninsdeletekey; Tasks: associatepmm
Root: HKCU; Subkey: "Software\Classes\MDA.MmdProject\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\Mmd.Modern.App.exe"",0"; Tasks: associatepmm
Root: HKCU; Subkey: "Software\Classes\MDA.MmdProject\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Mmd.Modern.App.exe"" --open ""%1"""; Tasks: associatepmm
Root: HKCU; Subkey: "Software\Classes\Applications\Mmd.Modern.App.exe\SupportedTypes"; ValueType: string; ValueName: ".pmm"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associatepmm
Root: HKCU; Subkey: "Software\Classes\.pmx\OpenWithProgids"; ValueType: string; ValueName: "MDA.PmxModel"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associatemodels
Root: HKCU; Subkey: "Software\Classes\.pmx"; ValueType: string; ValueName: ""; ValueData: "MDA.PmxModel"; Tasks: associatemodels; Check: CanClaimUnassignedExtension('.pmx')
Root: HKCU; Subkey: "Software\Classes\MDA.PmxModel"; ValueType: string; ValueName: ""; ValueData: "MMD PMX model"; Flags: uninsdeletekey; Tasks: associatemodels
Root: HKCU; Subkey: "Software\Classes\MDA.PmxModel\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\Mmd.Modern.App.exe"",0"; Tasks: associatemodels
Root: HKCU; Subkey: "Software\Classes\MDA.PmxModel\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Mmd.Modern.App.exe"" --open ""%1"""; Tasks: associatemodels
Root: HKCU; Subkey: "Software\Classes\Applications\Mmd.Modern.App.exe\SupportedTypes"; ValueType: string; ValueName: ".pmx"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associatemodels
Root: HKCU; Subkey: "Software\Classes\.pmd\OpenWithProgids"; ValueType: string; ValueName: "MDA.PmdModel"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associatemodels
Root: HKCU; Subkey: "Software\Classes\.pmd"; ValueType: string; ValueName: ""; ValueData: "MDA.PmdModel"; Tasks: associatemodels; Check: CanClaimUnassignedExtension('.pmd')
Root: HKCU; Subkey: "Software\Classes\MDA.PmdModel"; ValueType: string; ValueName: ""; ValueData: "MMD PMD model"; Flags: uninsdeletekey; Tasks: associatemodels
Root: HKCU; Subkey: "Software\Classes\MDA.PmdModel\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\Mmd.Modern.App.exe"",0"; Tasks: associatemodels
Root: HKCU; Subkey: "Software\Classes\MDA.PmdModel\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Mmd.Modern.App.exe"" --open ""%1"""; Tasks: associatemodels
Root: HKCU; Subkey: "Software\Classes\Applications\Mmd.Modern.App.exe\SupportedTypes"; ValueType: string; ValueName: ".pmd"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associatemodels
Root: HKCU; Subkey: "Software\Classes\.vmd\OpenWithProgids"; ValueType: string; ValueName: "MDA.VmdMotion"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associatemotions
Root: HKCU; Subkey: "Software\Classes\.vmd"; ValueType: string; ValueName: ""; ValueData: "MDA.VmdMotion"; Tasks: associatemotions; Check: CanClaimUnassignedExtension('.vmd')
Root: HKCU; Subkey: "Software\Classes\MDA.VmdMotion"; ValueType: string; ValueName: ""; ValueData: "MMD motion"; Flags: uninsdeletekey; Tasks: associatemotions
Root: HKCU; Subkey: "Software\Classes\MDA.VmdMotion\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\Mmd.Modern.App.exe"",0"; Tasks: associatemotions
Root: HKCU; Subkey: "Software\Classes\MDA.VmdMotion\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Mmd.Modern.App.exe"" --open ""%1"""; Tasks: associatemotions
Root: HKCU; Subkey: "Software\Classes\Applications\Mmd.Modern.App.exe\SupportedTypes"; ValueType: string; ValueName: ".vmd"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associatemotions
Root: HKCU; Subkey: "Software\Classes\.vpd\OpenWithProgids"; ValueType: string; ValueName: "MDA.VpdPose"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associateposes
Root: HKCU; Subkey: "Software\Classes\.vpd"; ValueType: string; ValueName: ""; ValueData: "MDA.VpdPose"; Tasks: associateposes; Check: CanClaimUnassignedExtension('.vpd')
Root: HKCU; Subkey: "Software\Classes\MDA.VpdPose"; ValueType: string; ValueName: ""; ValueData: "MMD pose"; Flags: uninsdeletekey; Tasks: associateposes
Root: HKCU; Subkey: "Software\Classes\MDA.VpdPose\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\Mmd.Modern.App.exe"",0"; Tasks: associateposes
Root: HKCU; Subkey: "Software\Classes\MDA.VpdPose\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Mmd.Modern.App.exe"" --open ""%1"""; Tasks: associateposes
Root: HKCU; Subkey: "Software\Classes\Applications\Mmd.Modern.App.exe\SupportedTypes"; ValueType: string; ValueName: ".vpd"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associateposes

[Icons]
Name: "{group}\MDA"; Filename: "{app}\Mmd.Modern.App.exe"; WorkingDir: "{app}"; AppUserModelID: "MDA.MikuDanceAdvance"
Name: "{autodesktop}\MDA"; Filename: "{app}\Mmd.Modern.App.exe"; WorkingDir: "{app}"; AppUserModelID: "MDA.MikuDanceAdvance"; Tasks: desktopicon

[Run]
Filename: "{app}\Mmd.Modern.App.exe"; Description: "{cm:LaunchProgram,MDA}"; Flags: nowait postinstall skipifsilent
Filename: "{code:DefaultAppsSettingsUri}"; Description: "{cm:OpenDefaultApps}"; Flags: shellexec nowait postinstall skipifsilent; Check: NeedsDefaultAppsSettings

; Personal files are managed below with an ownership marker and explicit uninstall
; consent. No wildcard [UninstallDelete] entry may remove a personal-data root.

[Messages]
english.SelectDirDesc=Where should the MDA program be installed?
english.SelectDirLabel3=This folder holds the MDA program. The next page lets you choose a separate location for settings, plugins, and recovery copies.
english.SelectComponentsLabel2=Shared files include the editor, runtime, and export tools. Engine sizes count only their additional files. Classic supports original MMD / MME effects.
english.SelectTasksLabel2=Select the file types to open with MDA. Unassigned types are associated automatically. If another app is the default, confirm your choice in Windows Settings after setup.
chinesesimplified.SelectDirDesc=MDA 程序安装到哪里？
chinesesimplified.SelectDirLabel3=这里保存 MDA 程序。下一页可单独选择设置、插件和自动恢复副本的保存位置。
chinesesimplified.SelectComponentsLabel2=公共组件包含编辑器、运行库和导出工具；引擎栏只计算各自新增的文件。传统引擎用于原版 MMD／MME 特效。
chinesesimplified.SelectTasksLabel2=勾选希望由 MDA 打开的文件类型。未指定默认应用的类型会自动关联；已有默认应用的类型可在安装完成后到 Windows 设置中更改。
japanese.SelectDirDesc=MDA プログラムのインストール先
japanese.SelectDirLabel3=MDA 本体を保存するフォルダーです。次のページで、設定・プラグイン・復元用コピーの保存先を別に選べます。
japanese.SelectComponentsLabel2=共通ファイルにはエディター、ランタイム、書き出しツールが含まれます。各エンジンのサイズは追加分です。従来エンジンは MMD / MME 用です。
japanese.SelectTasksLabel2=MDA で開くファイル形式を選んでください。既定のアプリがない形式は自動で関連付けられます。別のアプリが既定の場合は、インストール後に Windows の設定で変更できます。

[CustomMessages]
english.SharedComponent=Editor and shared files (includes FFmpeg)
english.ModernComponent=Modern rendering engine (Direct3D 12 and upscaling)
english.FileAssociations=File types to open with MDA
english.AssociationDescription=Create and edit MMD animation projects, models, motions, and poses.
english.OpenDefaultApps=Set MDA as default in Windows Settings
english.CustomInstallType=Choose components
english.associatepma=MDA Advance projects (.pma)
english.associatepmc=MDA Classic projects (.pmc)
english.associatepmm=MMD projects (.pmm)
english.associatemodels=MMD models (.pmx / .pmd)
english.associatemotions=MMD motions (.vmd)
english.associateposes=MMD poses (.vpd)
english.RelocationDriveMissing=The previous MDA installation is registered at %1, but that drive is not available. If it is a removable or network drive, choose No, reconnect it and run Setup again. Choose Yes only if that installation is gone for good: its uninstall entry will be removed.
english.RelocationFolderMissing=The previous MDA installation is registered at %1, but that folder was not found. If it is on a removable or network drive that now uses a different letter, choose No, reconnect it and run Setup again. Choose Yes only if that installation is gone for good: its uninstall entry will be removed.
english.RelocationRegistryLocked=The previous MDA uninstall entry could not be removed (access denied). Fix the permissions of the MDA entry under HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall and run Setup again.
english.RelocationLocationUnknown=The previous MDA installation is registered, but its folder cannot be determined from the uninstall entry. Uninstall it from Settings > Apps first (or remove the MDA entry under HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall if it is gone for good), then run Setup again.
english.RelocationDriveMissingKept=The previous MDA installation at %1 is on a drive that is not available. Reconnect it and run Setup again, or install to that same folder.
english.RelocationUninstallFailed=The previous MDA installation could not be removed. Close MDA and retry, or uninstall it before choosing a different folder.
english.UserDataTitle=Settings, plugins, and autosave
english.UserDataDescription=Choose a folder for MDA personal files, or keep the default.
english.UserDataPrompt=Settings, plugins, recovery copies, temporary files, and logs are kept here in separate folders. Existing settings and plugins are kept. Projects, source models, and exported videos stay where they are. You can keep the default location, or choose an MDA-only folder on another drive. Uninstall keeps these files by default.
english.UserDataLabel=Personal files folder:
english.UserDataInvalid=Choose a local folder outside the installation folder. Drive roots, network paths, redirected folders, and folders containing other files cannot be used. An existing MDA personal folder can be reused.
english.UserDataWriteFailed=MDA could not save the personal files location. Check the folder permissions and retry installation.
english.UserDataUninstallTitle=Keep your MDA personal files?
english.UserDataUninstallPrompt=This applies only to settings, plugins, autosave and recovery copies, temporary files, and logs in:%n%1%n%nProjects, original models, exported videos, and recovery copies in other folders are kept.
english.UserDataKeep=Keep personal files (default)
english.UserDataDelete=Delete personal files
english.UserDataCancel=Cancel uninstall
english.UserDataDeleteFailed=Some personal files could not be safely removed. Their location has been kept so that MDA can find them after reinstalling.
chinesesimplified.UserDataTitle=设置、插件与自动保存
chinesesimplified.UserDataDescription=选择 MDA 个人文件夹，也可以保留默认位置。
chinesesimplified.UserDataPrompt=设置、插件、自动保存副本、临时文件和日志会分类保存在这里。已有设置和插件会保留；工程、原始模型和导出视频留在原位置。通常直接使用默认位置即可；要放到其他磁盘，请选择一个 MDA 专用文件夹。卸载时默认保留这些文件。
chinesesimplified.UserDataLabel=个人文件保存位置：
chinesesimplified.UserDataInvalid=请选择安装目录以外的本地文件夹。不能使用磁盘根目录、网络路径、重定向文件夹或存有其他文件的目录。可以继续使用已有的 MDA 个人文件夹。
chinesesimplified.UserDataWriteFailed=无法保存 MDA 个人文件位置。请检查文件夹权限后重新安装。
chinesesimplified.UserDataUninstallTitle=保留 MDA 个人文件吗？
chinesesimplified.UserDataUninstallPrompt=此选择只影响以下位置中的设置、插件、自动保存与恢复副本、临时文件和日志：%n%1%n%n工程、原始模型、导出视频和其他文件夹中的恢复副本会保留。
chinesesimplified.UserDataKeep=保留个人文件（默认）
chinesesimplified.UserDataDelete=删除个人文件
chinesesimplified.UserDataCancel=取消卸载
chinesesimplified.UserDataDeleteFailed=部分个人文件无法安全删除。已保留其位置，重新安装后 MDA 仍可找到它们。
chinesesimplified.SharedComponent=编辑器与公共组件（含内置 FFmpeg）
chinesesimplified.ModernComponent=现代渲染引擎（Direct3D 12 与超分辨率）
chinesesimplified.FileAssociations=希望由 MDA 打开的文件类型
chinesesimplified.AssociationDescription=制作与编辑 MMD 动画工程、模型、动作和姿势。
chinesesimplified.OpenDefaultApps=在 Windows 设置中选择 MDA 为默认应用
chinesesimplified.CustomInstallType=自选安装组件
chinesesimplified.associatepma=MDA 现代工程（.pma）
chinesesimplified.associatepmc=MDA 传统工程（.pmc）
chinesesimplified.associatepmm=MMD 工程（.pmm）
chinesesimplified.associatemodels=MMD 模型（.pmx / .pmd）
chinesesimplified.associatemotions=MMD 动作（.vmd）
chinesesimplified.associateposes=MMD 姿势（.vpd）
chinesesimplified.RelocationDriveMissing=原来的 MDA 安装登记在 %1，但该磁盘当前不可用。如果它是移动硬盘或网络驱动器，请选“否”，重新连接后再运行安装程序。只有确定原安装已经不存在时才选“是”：它的卸载登记将被删除。
chinesesimplified.RelocationFolderMissing=原来的 MDA 安装登记在 %1，但没有找到该文件夹。如果它位于移动硬盘或网络驱动器上，而盘符已被其他磁盘占用，请选“否”，重新连接后再运行安装程序。只有确定原安装已经不存在时才选“是”：它的卸载登记将被删除。
chinesesimplified.RelocationRegistryLocked=无法删除原来的 MDA 卸载登记（拒绝访问）。请修复 HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall 下 MDA 项的权限后，再运行安装程序。
chinesesimplified.RelocationLocationUnknown=已登记原来的 MDA 安装，但无法从卸载登记中确定其所在文件夹。请先在“设置 > 应用”中卸载它（如果确定它已不存在，可删除 HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall 下的 MDA 项），然后再运行安装程序。
chinesesimplified.RelocationDriveMissingKept=原来的 MDA 安装位于 %1，所在磁盘当前不可用。请重新连接该磁盘后再运行安装程序，或安装到同一文件夹。
chinesesimplified.RelocationUninstallFailed=无法移除原来的 MDA 安装。请关闭 MDA 后重试，或先卸载原安装，再选择其他文件夹。
japanese.SharedComponent=エディターと共通ファイル（FFmpeg を含む）
japanese.ModernComponent=モダンエンジン（Direct3D 12 とアップスケーリング）
japanese.FileAssociations=MDA で開くファイル形式
japanese.AssociationDescription=MMD アニメーションのプロジェクト、モデル、モーション、ポーズを制作・編集します。
japanese.OpenDefaultApps=Windows の「既定のアプリ」で MDA を選ぶ
japanese.CustomInstallType=コンポーネントを選択
japanese.associatepma=MDA モダンプロジェクト（.pma）
japanese.associatepmc=MDA 従来プロジェクト（.pmc）
japanese.associatepmm=MMD プロジェクト（.pmm）
japanese.associatemodels=MMD モデル（.pmx / .pmd）
japanese.associatemotions=MMD モーション（.vmd）
japanese.associateposes=MMD ポーズ（.vpd）
japanese.RelocationDriveMissing=以前の MDA は %1 に登録されていますが、そのドライブは現在使用できません。リムーバブルまたはネットワーク ドライブの場合は「いいえ」を選び、再接続してからセットアップを再実行してください。以前のインストールが完全に失われている場合のみ「はい」を選んでください。アンインストール情報が削除されます。
japanese.RelocationFolderMissing=以前の MDA は %1 に登録されていますが、そのフォルダーが見つかりません。リムーバブルまたはネットワーク ドライブのドライブ文字が別のディスクに使われている場合は「いいえ」を選び、再接続してからセットアップを再実行してください。以前のインストールが完全に失われている場合のみ「はい」を選んでください。アンインストール情報が削除されます。
japanese.RelocationRegistryLocked=以前の MDA のアンインストール情報を削除できませんでした (アクセス拒否)。HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall にある MDA の項目のアクセス許可を修正してから、セットアップを再実行してください。
japanese.RelocationLocationUnknown=以前の MDA は登録されていますが、アンインストール情報からそのフォルダーを特定できません。先に「設定 > アプリ」からアンインストールしてください (完全に失われている場合は HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall にある MDA の項目を削除してください)。その後、セットアップを再実行してください。
japanese.RelocationDriveMissingKept=以前の MDA (%1) のドライブは現在使用できません。ドライブを再接続してからセットアップを再実行するか、同じフォルダーにインストールしてください。
japanese.RelocationUninstallFailed=以前の MDA を削除できませんでした。MDA を終了して再試行するか、以前のインストールをアンインストールしてから別のフォルダーを選択してください。
japanese.UserDataTitle=設定・プラグイン・自動保存
japanese.UserDataDescription=MDA 個人ファイルの保存先です。既定の場所も使えます。
japanese.UserDataPrompt=設定、プラグイン、復元用コピー、一時ファイル、ログをここでフォルダー別に保存します。既存の設定とプラグインは保持し、プロジェクト、元のモデル、書き出した動画は移動しません。通常は既定の場所で構いません。別のドライブを使う場合は MDA 専用フォルダーを選んでください。アンインストール時は既定で保持します。
japanese.UserDataLabel=個人ファイルのフォルダー：
japanese.UserDataInvalid=インストール先の外にあるローカルフォルダーを選択してください。ドライブのルート、ネットワークパス、リダイレクトされたフォルダー、他のファイルが入ったフォルダーは使用できません。既存の MDA 個人フォルダーは使用できます。
japanese.UserDataWriteFailed=MDA の個人ファイルの保存先を記録できませんでした。フォルダーのアクセス許可を確認して再インストールしてください。
japanese.UserDataUninstallTitle=MDA の個人ファイルを保持しますか？
japanese.UserDataUninstallPrompt=次の場所にある設定、プラグイン、自動保存と復元用コピー、一時ファイル、ログだけが対象です：%n%1%n%nプロジェクト、元のモデル、書き出した動画、他のフォルダーの復元用コピーは保持されます。
japanese.UserDataKeep=個人ファイルを保持（既定）
japanese.UserDataDelete=個人ファイルを削除
japanese.UserDataCancel=アンインストールを中止
japanese.UserDataDeleteFailed=一部の個人ファイルを安全に削除できませんでした。再インストール後に MDA が見つけられるよう、保存先の記録を保持しました。

#ifdef HasClassic
english.ClassicComponent=Classic MMD / MME render engine (optional)
english.ClassicLicenseTitle=Classic component runtime license
english.ClassicLicenseDescription=The Classic component includes Microsoft.DXSDK.D3DX 9.29.952.8.
english.ClassicLicensePrompt=Read the Microsoft terms below. These runtime files retain their own license.
english.ClassicLicenseAccept=I accept the Microsoft runtime license terms.
english.ClassicLicenseRequired=Accept the Microsoft runtime license terms to install the Classic component.
chinesesimplified.ClassicComponent=传统 MMD / MME 渲染引擎（可选）
chinesesimplified.ClassicLicenseTitle=传统引擎运行库许可
chinesesimplified.ClassicLicenseDescription=传统引擎组件包含 Microsoft.DXSDK.D3DX 9.29.952.8。
chinesesimplified.ClassicLicensePrompt=请阅读下列 Microsoft 条款。运行库文件保留其独立许可。
chinesesimplified.ClassicLicenseAccept=我接受 Microsoft 运行库许可条款。
chinesesimplified.ClassicLicenseRequired=安装传统引擎组件需要接受 Microsoft 运行库许可条款。
japanese.ClassicComponent=従来の MMD / MME レンダリングエンジン（任意）
japanese.ClassicLicenseTitle=従来エンジンのランタイムライセンス
japanese.ClassicLicenseDescription=従来エンジンには Microsoft.DXSDK.D3DX 9.29.952.8 が含まれます。
japanese.ClassicLicensePrompt=以下の Microsoft の条項をお読みください。ランタイムには独自のライセンスが適用されます。
japanese.ClassicLicenseAccept=Microsoft ランタイムのライセンス条項に同意します。
japanese.ClassicLicenseRequired=従来エンジンをインストールするには、Microsoft ランタイムのライセンス条項への同意が必要です。

#endif

[Code]
const
  UserDataRegistryKey = 'Software\MDA\MikuDanceAdvance';
  UserDataMarkerName = '.mda-user-data.ini';
  UserDataAppId = 'MDA.MikuDanceAdvance';

var
  UserDataPage: TInputDirWizardPage;
  OriginalUserDataRoot, OriginalPreviousUserDataRoot: String;
  DeletePersonalFiles: Boolean;
  UninstallUserDataRoot: String;
#ifdef HasClassic
  ClassicLicensePage: TOutputMsgMemoWizardPage;
  ClassicLicenseAccepted: TNewCheckBox;
#endif

function UserDataGetFileAttributes(FileName: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function HasFileAssociationTasks: Boolean;
begin
  Result := WizardIsTaskSelected('associatepma') or WizardIsTaskSelected('associatepmc') or
    WizardIsTaskSelected('associatepmm') or WizardIsTaskSelected('associatemodels') or
    WizardIsTaskSelected('associatemotions') or WizardIsTaskSelected('associateposes');
end;

function CanClaimUnassignedExtension(Extension: String): Boolean;
var
  Existing: String;
begin
  { Windows ignores an extension default whose ProgID is no longer registered.
    Repair that fallback as well as empty values. Explorer's UserChoice takes
    precedence over this registration and is managed by Windows. }
  Result := not RegQueryStringValue(HKCR, Extension, '', Existing) or
    (Existing = '') or not RegKeyExists(HKCR, Existing);
end;

function AssocQueryString(Flags, Kind: Cardinal; Association, Verb, Buffer: String;
  var Characters: Cardinal): Integer;
  external 'AssocQueryStringW@shlwapi.dll stdcall';

function NeedsDefaultConfirmation(Task, Extension: String): Boolean;
var
  Characters: Cardinal;
  Buffer: String;
begin
  Result := False;
  if not WizardIsTaskSelected(Task) then exit;
  { ASSOCSTR_EXECUTABLE for the effective open verb, including the user's choice. }
  Characters := 32768;
  SetLength(Buffer, Characters);
  Result := AssocQueryString(0, 2, Extension, 'open', Buffer, Characters) <> 0;
  if not Result then
    Result := CompareText(Copy(Buffer, 1, Characters - 1),
      ExpandConstant('{app}\Mmd.Modern.App.exe')) <> 0;
end;

function NeedsDefaultAppsSettings: Boolean;
begin
  Result := NeedsDefaultConfirmation('associatepma', '.pma') or
    NeedsDefaultConfirmation('associatepmc', '.pmc') or
    NeedsDefaultConfirmation('associatepmm', '.pmm') or
    NeedsDefaultConfirmation('associatemodels', '.pmx') or
    NeedsDefaultConfirmation('associatemodels', '.pmd') or
    NeedsDefaultConfirmation('associatemotions', '.vmd') or
    NeedsDefaultConfirmation('associateposes', '.vpd');
end;

function DefaultAppsSettingsUri(Param: String): String;
var
  Version: TWindowsVersion;
begin
  Result := 'ms-settings:defaultapps';
  GetWindowsVersionEx(Version);
  { App-specific links are documented for Windows 11 with the April 2023 update
    or later. Windows 10 and older Windows 11 builds use the general page. }
  if (Version.Major > 10) or ((Version.Major = 10) and (Version.Build >= 22631)) then
    Result := Result + '?registeredAppUser=MDA.MikuDanceAdvance';
end;

function NormalizeUserDataPath(Value: String): String;
begin
  Result := RemoveBackslashUnlessRoot(ExpandFileName(Value));
end;

function SameUserDataPath(Left, Right: String): Boolean;
begin
  Result := CompareText(NormalizeUserDataPath(Left), NormalizeUserDataPath(Right)) = 0;
end;

function PathsOverlap(Left, Right: String): Boolean;
var
  A, B: String;
begin
  A := AddBackslash(NormalizeUserDataPath(Left));
  B := AddBackslash(NormalizeUserDataPath(Right));
  Result := (CompareText(Copy(A, 1, Length(B)), B) = 0) or
    (CompareText(Copy(B, 1, Length(A)), A) = 0);
end;

function IsPlainLocalUserDataPath(Value: String): Boolean;
var
  I, ComponentStart: Integer;
  Component: String;
begin
  Result := False;
  Value := RemoveBackslashUnlessRoot(Value);
  { Exclude relative, UNC, device, drive-root and alternate-stream paths before
    normalization. Reject Win32 aliases such as trailing dots/spaces as well. }
  if (Length(Value) <= 3) or (Copy(Value, 2, 2) <> ':\') or
    (Pos(Uppercase(Copy(Value, 1, 1)), 'ABCDEFGHIJKLMNOPQRSTUVWXYZ') = 0) then
    Exit;
  ComponentStart := 4;
  for I := 4 to Length(Value) + 1 do
  begin
    if I <= Length(Value) then
      if (Ord(Value[I]) < 32) or (Pos(Value[I], '/:*?"<>|') > 0) then
        Exit;
    if (I > Length(Value)) or (Copy(Value, I, 1) = '\') then
    begin
      Component := Copy(Value, ComponentStart, I - ComponentStart);
      if (Component = '') or (Component = '.') or (Component = '..') or
        (Copy(Component, Length(Component), 1) = '.') or
        (Copy(Component, Length(Component), 1) = ' ') then
        Exit;
      ComponentStart := I + 1;
    end;
  end;
  Result := True;
end;

function HasNoUserDataReparsePoints(Value: String): Boolean;
var
  Current, Parent: String;
  Attributes: LongWord;
  ErrorCode: Integer;
begin
  Result := False;
  Current := NormalizeUserDataPath(Value);
  while Current <> '' do
  begin
    Attributes := UserDataGetFileAttributes(Current);
    if Attributes = $FFFFFFFF then
    begin
      ErrorCode := DLLGetLastError;
      if (ErrorCode <> 2) and (ErrorCode <> 3) then
        Exit;
    end
    else
      if (Attributes and FILE_ATTRIBUTE_REPARSE_POINT) <> 0 then
        Exit;
    Parent := ExtractFileDir(Current);
    if CompareText(Parent, Current) = 0 then
      Break;
    Current := Parent;
  end;
  Result := True;
end;

function HasUserDataMarker(Root: String): Boolean;
var
  Marker: String;
begin
  Marker := AddBackslash(Root) + UserDataMarkerName;
  Result := HasNoUserDataReparsePoints(Marker) and FileExists(Marker) and
    (GetIniString('MDA', 'AppId', '', Marker) = UserDataAppId) and
    (GetIniString('MDA', 'LayoutVersion', '', Marker) = '1');
end;

function UserDataDirectoryEmpty(Root: String): Boolean;
var
  Entry: TFindRec;
begin
  Result := True;
  if FindFirst(AddBackslash(Root) + '*', Entry) then
  begin
    try
      repeat
        if (Entry.Name <> '.') and (Entry.Name <> '..') then
        begin
          Result := False;
          Break;
        end;
      until not FindNext(Entry);
    finally
      FindClose(Entry);
    end;
  end;
end;

function IsSafeUserDataCategory(Root, Category: String): Boolean;
var
  Path: String;
begin
  Path := AddBackslash(Root) + Category;
  Result := not FileExists(Path) and HasNoUserDataReparsePoints(Path);
end;

function IsValidUserDataRoot(Value, InstallDir: String): Boolean;
var
  Root, PreviousInstallDir, Marker: String;
begin
  Result := False;
  if not IsPlainLocalUserDataPath(Value) then
    Exit;
  Root := NormalizeUserDataPath(Value);
  if FileExists(Root) or not HasNoUserDataReparsePoints(Root) or
    PathsOverlap(Root, InstallDir) then
    Exit;
  if RegQueryStringValue(HKCU,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#SetupSetting("AppId")}_is1',
    'InstallLocation', PreviousInstallDir) and (PreviousInstallDir <> '') then
    if PathsOverlap(Root, PreviousInstallDir) then
      Exit;
  Marker := AddBackslash(Root) + UserDataMarkerName;
  if FileExists(Marker) or DirExists(Marker) then
  begin
    if not HasUserDataMarker(Root) then
      Exit;
  end
  else if DirExists(Root) and not UserDataDirectoryEmpty(Root) and
    not SameUserDataPath(Root, ExpandConstant('{localappdata}\MDA')) then
    Exit;
  { These are the only managed data categories. A junction here must not redirect
    either first-launch migration or a later explicit uninstall cleanup. }
  if not IsSafeUserDataCategory(Root, 'Config') or
    not IsSafeUserDataCategory(Root, 'Plugins') or
    not IsSafeUserDataCategory(Root, 'Recovery') or
    not IsSafeUserDataCategory(Root, 'Cache') or
    not IsSafeUserDataCategory(Root, 'Logs') then
    Exit;
  Result := True;
end;

procedure InitializeWizard;
#ifdef HasClassic
var
  LicenseText: AnsiString;
#endif
begin
  OriginalUserDataRoot := ExpandConstant('{localappdata}\MDA');
  RegQueryStringValue(HKCU, UserDataRegistryKey, 'UserDataRoot', OriginalUserDataRoot);
  if OriginalUserDataRoot = '' then
    OriginalUserDataRoot := ExpandConstant('{localappdata}\MDA');
  RegQueryStringValue(HKCU, UserDataRegistryKey, 'PreviousUserDataRoot', OriginalPreviousUserDataRoot);
  { Official CodeDlg.iss: https://github.com/jrsoftware/issrc/blob/main/Examples/CodeDlg.iss }
  UserDataPage := CreateInputDirPage(wpSelectDir,
    CustomMessage('UserDataTitle'), CustomMessage('UserDataDescription'),
    CustomMessage('UserDataPrompt'), False, 'MDA');
  UserDataPage.Add(CustomMessage('UserDataLabel'));
  UserDataPage.Values[0] := ExpandConstant('{param:USERDATAROOT}');
  if UserDataPage.Values[0] = '' then
    UserDataPage.Values[0] := OriginalUserDataRoot;
#ifdef HasClassic
  ExtractTemporaryFile('mda-classic-microsoft-license.txt');
  if not LoadStringFromFile(ExpandConstant('{tmp}\mda-classic-microsoft-license.txt'), LicenseText) then
    RaiseException('The Classic runtime license could not be read.');
  ClassicLicensePage := CreateOutputMsgMemoPage(wpSelectComponents,
    CustomMessage('ClassicLicenseTitle'), CustomMessage('ClassicLicenseDescription'),
    CustomMessage('ClassicLicensePrompt'), String(LicenseText));
  ClassicLicensePage.RichEditViewer.Height := ClassicLicensePage.SurfaceHeight - ClassicLicensePage.RichEditViewer.Top - ScaleY(36);
  ClassicLicenseAccepted := TNewCheckBox.Create(ClassicLicensePage);
  ClassicLicenseAccepted.Parent := ClassicLicensePage.Surface;
  ClassicLicenseAccepted.Top := ClassicLicensePage.SurfaceHeight - ScaleY(28);
  ClassicLicenseAccepted.Width := ClassicLicensePage.SurfaceWidth;
  ClassicLicenseAccepted.Height := ScaleY(20);
  ClassicLicenseAccepted.Caption := CustomMessage('ClassicLicenseAccept');
#endif
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
#ifdef HasClassic
  { Silent installs acknowledge the same terms in PrepareToInstall. }
  Result := (PageID = ClassicLicensePage.ID) and
    (WizardSilent or not WizardIsComponentSelected('classic'));
#endif
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = UserDataPage.ID then
  begin
    Result := IsValidUserDataRoot(UserDataPage.Values[0], WizardDirValue);
    if not Result then
      MsgBox(CustomMessage('UserDataInvalid'), mbInformation, MB_OK);
  end;
#ifdef HasClassic
  if (CurPageID = ClassicLicensePage.ID) and WizardIsComponentSelected('classic') and not ClassicLicenseAccepted.Checked then
  begin
    MsgBox(CustomMessage('ClassicLicenseRequired'), mbInformation, MB_OK);
    Result := False;
  end;
#endif
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo + NewLine + Space + CustomMessage('UserDataLabel') +
    ' ' + UserDataPage.Values[0] + NewLine + NewLine + MemoTypeInfo +
    NewLine + MemoComponentsInfo + NewLine + MemoGroupInfo + NewLine + MemoTasksInfo;
end;

procedure SaveUserDataLocation;
var
  Root, Marker, MigrationSource: String;
begin
  Root := NormalizeUserDataPath(UserDataPage.Values[0]);
  if not IsValidUserDataRoot(Root, WizardDirValue) or not ForceDirectories(Root) then
    RaiseException(CustomMessage('UserDataWriteFailed'));
  Marker := AddBackslash(Root) + UserDataMarkerName;
  { Never rewrite an existing marker: Initialized belongs to the App's migration
    transaction, and setup must not reset it during upgrades/reinstallation. }
  if not FileExists(Marker) then
    if not SaveStringToFile(Marker,
      '[MDA]'#13#10'AppId=' + UserDataAppId + #13#10'LayoutVersion=1'#13#10, False) then
      RaiseException(CustomMessage('UserDataWriteFailed'));
  if not HasUserDataMarker(Root) then
    RaiseException(CustomMessage('UserDataWriteFailed'));
  if not SameUserDataPath(Root, OriginalUserDataRoot) then
  begin
    MigrationSource := OriginalUserDataRoot;
    { Repeated installs before first launch keep the original migration source. }
    if HasUserDataMarker(OriginalUserDataRoot) and
      (GetIniString('MDA', 'Initialized', '', AddBackslash(OriginalUserDataRoot) + UserDataMarkerName) <> '1') and
      (OriginalPreviousUserDataRoot <> '') then
      MigrationSource := OriginalPreviousUserDataRoot;
    if not SameUserDataPath(Root, MigrationSource) then
      if not RegWriteStringValue(HKCU, UserDataRegistryKey, 'PreviousUserDataRoot', MigrationSource) then
        RaiseException(CustomMessage('UserDataWriteFailed'));
  end;
  if not RegWriteStringValue(HKCU, UserDataRegistryKey, 'UserDataRoot', Root) then
    RaiseException(CustomMessage('UserDataWriteFailed'));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    SaveUserDataLocation;
end;

{ Deletes the stale registration; a key that cannot be deleted would fail Setup after its files are written, so report
  it now instead. }
function DropUninstallRegistration(const UninstallKey: String): String;
begin
  Result := '';
  if not RegDeleteKeyIncludingSubkeys(HKCU, UninstallKey) and RegKeyExists(HKCU, UninstallKey) then
    Result := CustomMessage('RelocationRegistryLocked');
end;

{ The executable path at the start of an UninstallString, or ''. Inno writes it quoted ("C:\...\unins000.exe" /args);
  an unquoted one (C:\...\unins000.exe /args, possibly with spaces in the folder) ends at its first ".exe". }
function UninstallerFromCommand(const Command: String): String;
var
  QuoteEnd, ExeEnd: Integer;
  Trimmed: String;
begin
  Result := '';
  Trimmed := Trim(Command);
  if Copy(Trimmed, 1, 1) = '"' then
  begin
    QuoteEnd := Pos('"', Copy(Trimmed, 2, Length(Trimmed)));
    if QuoteEnd > 0 then
      Result := Copy(Trimmed, 2, QuoteEnd - 1);
  end
  else
  begin
    ExeEnd := Pos('.exe', Lowercase(Trimmed));
    if ExeEnd > 0 then
      Result := Copy(Trimmed, 1, ExeEnd + 3);
  end;
end;

function RemoveRelocatedInstallation: String;
var
  UninstallKey, PreviousDir, PreviousCommand, PreviousUninstaller: String;
  ResultCode: Integer;
begin
  UninstallKey := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#SetupSetting("AppId")}_is1';
  Result := '';
  if not RegKeyExists(HKCU, UninstallKey) then
    Exit;
  Result := CustomMessage('RelocationUninstallFailed');
  { Inno writes InstallLocation, "Inno Setup: App Path" and UninstallString separately. Recover the old folder from
    any of them before treating the registration as empty; only one that names nothing at all is dropped. }
  if not RegQueryStringValue(HKCU, UninstallKey, 'InstallLocation', PreviousDir) then
    PreviousDir := '';
  if PreviousDir = '' then
    if not RegQueryStringValue(HKCU, UninstallKey, 'Inno Setup: App Path', PreviousDir) then
      PreviousDir := '';
  if (PreviousDir = '') and RegQueryStringValue(HKCU, UninstallKey, 'UninstallString', PreviousCommand) then
  begin
    PreviousUninstaller := UninstallerFromCommand(PreviousCommand);
    if PreviousUninstaller <> '' then
      PreviousDir := ExtractFileDir(PreviousUninstaller);
  end;
  if PreviousDir = '' then
  begin
    { No location, app path or parseable uninstaller: the old installation cannot be located, so it can be neither
      uninstalled nor shown to be gone. Keep the registration and stop rather than orphan files that may still exist. }
    Log('Keeping MDA uninstall registration without a recoverable install location.');
    Result := CustomMessage('RelocationLocationUnknown');
    Exit;
  end;
  PreviousDir := AddBackslash(ExpandFileName(PreviousDir));
  if CompareText(PreviousDir, AddBackslash(ExpandFileName(WizardDirValue))) = 0 then
  begin
    { Same-directory upgrades keep Inno's existing uninstall-log append flow. }
    Result := '';
    Exit;
  end;
  if not FileExists(PreviousDir + 'Mmd.Modern.App.exe') and not DirExists(PreviousDir) then
  begin
    { A missing folder does not prove the installation is gone: its drive may be disconnected, or the letter may now
      belong to another volume while the original one is unplugged. Setup cannot confirm the volume, so it never
      drops the registration on its own: a silent install keeps it and stops, an interactive one asks first. }
    if WizardSilent then
    begin
      Result := FmtMessage(CustomMessage('RelocationDriveMissingKept'), [PreviousDir]);
      Exit;
    end;
    if not DirExists(AddBackslash(ExtractFileDrive(PreviousDir))) then
    begin
      if SuppressibleMsgBox(FmtMessage(CustomMessage('RelocationDriveMissing'), [PreviousDir]),
        mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) <> IDYES then
      begin
        Result := FmtMessage(CustomMessage('RelocationDriveMissingKept'), [PreviousDir]);
        Exit;
      end;
    end
    else if SuppressibleMsgBox(FmtMessage(CustomMessage('RelocationFolderMissing'), [PreviousDir]),
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) <> IDYES then
    begin
      Result := FmtMessage(CustomMessage('RelocationDriveMissingKept'), [PreviousDir]);
      Exit;
    end;
    Log('User abandoned the MDA installation registered at: ' + PreviousDir);
    Result := DropUninstallRegistration(UninstallKey);
    Exit;
  end;
  if not RegQueryStringValue(HKCU, UninstallKey, 'UninstallString', PreviousCommand) then
    Exit;
  PreviousUninstaller := UninstallerFromCommand(PreviousCommand);
  if not FileExists(PreviousUninstaller) or
    (CompareText(AddBackslash(ExtractFileDir(ExpandFileName(PreviousUninstaller))), PreviousDir) <> 0) or
    (CompareText(Copy(ExtractFileName(PreviousUninstaller), 1, 5), 'unins') <> 0) or
    (CompareText(ExtractFileExt(PreviousUninstaller), '.exe') <> 0) then
    Exit;
  { Run the validated executable quoted, so an unquoted path with spaces cannot be split by CreateProcess. }
  PreviousCommand := Trim(PreviousCommand);
  if Copy(PreviousCommand, 1, 1) <> '"' then
    PreviousCommand := AddQuotes(PreviousUninstaller) + Copy(PreviousCommand, Length(PreviousUninstaller) + 1, Length(PreviousCommand));
  { Official Exec supports a registered command line via '>':
    https://jrsoftware.org/ishelp/topic_isxfunc_exec.htm
    Let the original uninstall log remove only its tracked payload; never delete
    the old directory recursively or touch user projects/preferences. }
  Log('Removing previous MDA installation before relocation: ' + PreviousDir);
  if not Exec('>', PreviousCommand + ' /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /KEEPUSERDATA=yes /LOG',
    ExpandConstant('{tmp}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Log('Previous MDA uninstaller could not start: ' + SysErrorMessage(ResultCode));
    Exit;
  end;
  Log('Previous MDA uninstaller exit code: ' + IntToStr(ResultCode));
  if (ResultCode <> 0) or RegKeyExists(HKCU, UninstallKey) or
    FileExists(PreviousDir + 'Mmd.Modern.App.exe') then
    Exit;
  Result := '';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
#ifdef HasClassic
  if WizardIsComponentSelected('classic') then
  begin
    if WizardSilent then
    begin
      { Silent deployments must explicitly acknowledge the same included license. }
      if CompareText(ExpandConstant('{param:ACCEPTCLASSICLICENSE|no}'), 'yes') <> 0 then
        Result := CustomMessage('ClassicLicenseRequired') + ' /ACCEPTCLASSICLICENSE=yes';
    end
    else if not ClassicLicenseAccepted.Checked then
      Result := CustomMessage('ClassicLicenseRequired');
  end;
#endif
  if (Result = '') and not IsValidUserDataRoot(UserDataPage.Values[0], WizardDirValue) then
    Result := CustomMessage('UserDataInvalid');
  { Refusing a Classic license must leave the previous installation intact. }
  if Result = '' then
    Result := RemoveRelocatedInstallation;
end;

function InitializeUninstall: Boolean;
var
  Choice: Integer;
begin
  Result := True;
  DeletePersonalFiles := False;
  UninstallUserDataRoot := ExpandConstant('{localappdata}\MDA');
  RegQueryStringValue(HKCU, UserDataRegistryKey, 'UserDataRoot', UninstallUserDataRoot);
  if UninstallUserDataRoot = '' then
    UninstallUserDataRoot := ExpandConstant('{localappdata}\MDA');
  { A silent uninstall, including install-directory relocation, always keeps data.
    There is intentionally no command-line option that silently enables deletion. }
  if UninstallSilent or
    (CompareText(ExpandConstant('{param:KEEPUSERDATA|no}'), 'yes') = 0) then
    Exit;
  { Official TaskDialogMsgBox supports custom labels; the first/default button is
    keep, the second is explicit delete. Escape/Close cancels uninstall. }
  Choice := TaskDialogMsgBox(CustomMessage('UserDataUninstallTitle'),
    FmtMessage(CustomMessage('UserDataUninstallPrompt'), [UninstallUserDataRoot]),
    mbConfirmation, MB_YESNOCANCEL, [CustomMessage('UserDataKeep'), CustomMessage('UserDataDelete'),
     CustomMessage('UserDataCancel')], 0);
  Result := (Choice = IDYES) or (Choice = IDNO);
  DeletePersonalFiles := Choice = IDNO;
end;

function RemoveOwnedUserDataPath(Root, Target: String; Depth: Integer): Boolean;
var
  Entry: TFindRec;
  Attributes: LongWord;
  Prefix: String;
  ErrorCode: Integer;
begin
  Result := False;
  Prefix := AddBackslash(NormalizeUserDataPath(Root));
  Target := NormalizeUserDataPath(Target);
  { The root itself is never a recursive deletion target. Check every descended
    path again so that directory junctions and symbolic links are never followed. }
  if (Depth > 128) or
    (CompareText(Copy(Target, 1, Length(Prefix)), Prefix) <> 0) or
    not HasNoUserDataReparsePoints(Target) then
  begin
    Log('Keeping unsafe personal-data path: ' + Target);
    Exit;
  end;
  Attributes := UserDataGetFileAttributes(Target);
  if Attributes = $FFFFFFFF then
  begin
    ErrorCode := DLLGetLastError;
    Result := (ErrorCode = 2) or (ErrorCode = 3);
    Exit;
  end;
  if (Attributes and FILE_ATTRIBUTE_REPARSE_POINT) <> 0 then
    Exit;
  if (Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0 then
  begin
    Result := DeleteFile(Target);
    Exit;
  end;
  Result := True;
  if FindFirst(AddBackslash(Target) + '*', Entry) then
  begin
    try
      repeat
        if (Entry.Name <> '.') and (Entry.Name <> '..') then
          if not RemoveOwnedUserDataPath(Root, AddBackslash(Target) + Entry.Name, Depth + 1) then
            Result := False;
      until not FindNext(Entry);
    finally
      FindClose(Entry);
    end;
  end;
  if Result then
    Result := RemoveDir(Target);
end;

procedure RemovePersonalFiles;
var
  Root, CurrentLocator, Marker: String;
  Success: Boolean;
begin
  Root := UninstallUserDataRoot;
  Success := False;
  if IsPlainLocalUserDataPath(Root) then
    if HasNoUserDataReparsePoints(Root) and HasUserDataMarker(Root) and
      not PathsOverlap(Root, ExpandConstant('{app}')) then
    begin
      Root := NormalizeUserDataPath(Root);
      { Only this layout's five owned categories and the exact legacy settings
        files are removed. Never traverse PreviousUserDataRoot or JSON history. }
      Success := RemoveOwnedUserDataPath(Root, AddBackslash(Root) + 'Config', 0);
      if not RemoveOwnedUserDataPath(Root, AddBackslash(Root) + 'Plugins', 0) then
        Success := False;
      if not RemoveOwnedUserDataPath(Root, AddBackslash(Root) + 'Recovery', 0) then
        Success := False;
      if not RemoveOwnedUserDataPath(Root, AddBackslash(Root) + 'Cache', 0) then
        Success := False;
      if not RemoveOwnedUserDataPath(Root, AddBackslash(Root) + 'Logs', 0) then
        Success := False;
      if not RemoveOwnedUserDataPath(Root, AddBackslash(Root) + 'editor-preferences.json', 0) then
        Success := False;
      if not RemoveOwnedUserDataPath(Root, AddBackslash(Root) + 'editor-preview.json', 0) then
        Success := False;
      if not RemoveOwnedUserDataPath(Root, AddBackslash(Root) + 'editor-output.json', 0) then
        Success := False;
      { ProjectCache is an old app-owned cache, only at the old default location. }
      if SameUserDataPath(Root, ExpandConstant('{localappdata}\MDA')) then
        if not RemoveOwnedUserDataPath(Root, AddBackslash(Root) + 'ProjectCache', 0) then
          Success := False;
      if Success then
      begin
        Marker := AddBackslash(Root) + UserDataMarkerName;
        Success := HasUserDataMarker(Root) and DeleteFile(Marker);
        if Success and UserDataDirectoryEmpty(Root) then
          Success := RemoveDir(Root);
      end;
    end;
  if Success then
  begin
    { Preserve a locator changed by a newer install or another running session. }
    if RegQueryStringValue(HKCU, UserDataRegistryKey, 'UserDataRoot', CurrentLocator) then
      if SameUserDataPath(CurrentLocator, Root) then
      begin
        RegDeleteValue(HKCU, UserDataRegistryKey, 'UserDataRoot');
        RegDeleteValue(HKCU, UserDataRegistryKey, 'PreviousUserDataRoot');
      end;
  end
  else
  begin
    Log('Personal-data removal incomplete; keeping the location registry values: ' + Root);
    if not UninstallSilent then
      MsgBox(CustomMessage('UserDataDeleteFailed'), mbInformation, MB_OK);
  end;
end;

procedure RemoveOwnedAssociationDefault(Extension, ProgId: String);
var
  CurrentDefault, CurrentCommand: String;
begin
  { Never erase another application's extension default, even if changed after install. }
  if RegQueryStringValue(HKCU, 'Software\Classes\' + Extension, '', CurrentDefault) and
    (CompareText(CurrentDefault, ProgId) = 0) and
    RegQueryStringValue(HKCU, 'Software\Classes\' + ProgId + '\shell\open\command', '', CurrentCommand) and
    (CompareText(CurrentCommand, ExpandConstant('"{app}\Mmd.Modern.App.exe" --open "%1"')) = 0) then
    RegDeleteValue(HKCU, 'Software\Classes\' + Extension, '');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    RemoveOwnedAssociationDefault('.pma', 'MDA.AdvanceProject');
    RemoveOwnedAssociationDefault('.pmc', 'MDA.ClassicProject');
    RemoveOwnedAssociationDefault('.pmm', 'MDA.MmdProject');
    RemoveOwnedAssociationDefault('.pmx', 'MDA.PmxModel');
    RemoveOwnedAssociationDefault('.pmd', 'MDA.PmdModel');
    RemoveOwnedAssociationDefault('.vmd', 'MDA.VmdMotion');
    RemoveOwnedAssociationDefault('.vpd', 'MDA.VpdPose');
  end;
  if (CurUninstallStep = usPostUninstall) and DeletePersonalFiles then
    RemovePersonalFiles;
end;
