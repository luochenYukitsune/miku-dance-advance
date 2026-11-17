# Run on the disposable Windows packaging runner, using the setup just built.
param([Parameter(Mandatory)][string]$Installer, [Parameter(Mandatory)][string]$WorkDirectory)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw '文件关联安装测试需要 GitHub 托管的临时 Windows 运行器。'
}
$work = (New-Item -ItemType Directory -Path $WorkDirectory).FullName
$app = Join-Path $work 'MDA App'
$inf = Join-Path $work 'setup.inf'
$classes = 'HKEY_CURRENT_USER\Software\Classes'
foreach ($ext in '.pma', '.pmc', '.pmm') {
    $machineDefault = [Microsoft.Win32.Registry]::GetValue("HKEY_LOCAL_MACHINE\Software\Classes\$ext", '', '')
    Write-Output "运行器原有系统关联 $ext = '$machineDefault'"
}
# Override only the disposable runner's per-user defaults. The image may ship
# machine-wide PMA/PMC handlers; the test should exercise the merged HKCR view.
[Microsoft.Win32.Registry]::SetValue("$classes\.pma", '', '')
[Microsoft.Win32.Registry]::SetValue("$classes\.pmc", '', 'MDA.Test.RemovedHandler')
# A selected type that already has a registered handler keeps that default.
[Microsoft.Win32.Registry]::SetValue("$classes\.pmm", '', 'MDA.Test.OtherHandler')
$otherExe = (Get-Process -Id $PID).Path
[Microsoft.Win32.Registry]::SetValue("$classes\MDA.Test.OtherHandler\shell\open\command", '', ('"{0}" "%1"' -f $otherExe))

$argsList = @('/VERYSILENT', '/SP-', '/SUPPRESSMSGBOXES', '/NORESTART', '/LANG=chinesesimplified',
    '/COMPONENTS=editor,d3d12', '/TASKS=associatepma,associatepmc,associatepmm', "/DIR=`"$app`"",
    "/USERDATAROOT=`"$work\MDA Data`"", "/SAVEINF=`"$inf`"", "/LOG=`"$work\setup.log`"")
$install = Start-Process -FilePath $Installer -ArgumentList $argsList -WindowStyle Hidden -Wait -PassThru
if ($install.ExitCode -ne 0) {
    Get-Content -LiteralPath "$work\setup.log" -Tail 40 -ErrorAction SilentlyContinue
    throw "安装失败：$($install.ExitCode)"
}
if (-not (Select-String -LiteralPath $inf -Pattern '^Lang=chinesesimplified$' -Quiet)) { throw '安装包未选中简体中文' }

Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
using System.Text;
public static class ShellAssociation {
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int AssocQueryStringW(uint flags, uint kind, string ext, string verb, StringBuilder result, ref uint length);
}
'@
function Get-ShellOpenValue([string]$Extension, [uint32]$Kind) {
    $size = [uint32]32768
    $value = [Text.StringBuilder]::new([int]$size)
    $hr = [ShellAssociation]::AssocQueryStringW(0, $Kind, $Extension, 'open', $value, [ref]$size)
    if ($hr -ne 0) { throw "Windows 解析 $Extension 的打开方式失败：HRESULT=$hr" }
    return $value.ToString()
}
$editor = Join-Path $app 'Mmd.Modern.App.exe'
if (-not (Test-Path -LiteralPath $editor -PathType Leaf)) { throw "缺少已安装的编辑器：$editor" }
foreach ($ext in '.pma', '.pmc') {
    # ASSOCSTR_COMMAND (1) and ASSOCSTR_EXECUTABLE (2) for the effective Shell open verb.
    $command = Get-ShellOpenValue $ext 1
    $exe = Get-ShellOpenValue $ext 2
    if ($exe -ne $editor -or $command -ne ('"{0}" --open "%1"' -f $editor)) {
        throw "Windows 的 $ext 打开方式与本次安装不符：程序=$exe，命令=$command"
    }
}
if ((Get-ShellOpenValue '.pmm' 2) -ne $otherExe) {
    throw '已有的 PMM 打开程序发生变化'
}
Write-Output '安装包简体中文、PMA/PMC 的有效打开程序及 PMM 的已有默认应用均已验证。'
