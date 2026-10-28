# 阅画 Yuehua - NativeAOT 一键发布脚本
# 用法（PowerShell）：  .\tools\build-aot.ps1
#
# 背景：AOT 链接阶段需要 Windows SDK 导入库（advapi32.lib 等）与 MSVC 的 link.exe。
#   正常开发机由 MSBuild 通过注册表/VS 安装信息自动定位；但在受限环境（reg.exe 被安全
#   策略拦截、或环境变量缺失）会出现两类失败：
#     · "Platform linker not found"  —— findvcvarsall.bat 探测失败
#     · "LNK1181: cannot open advapi32.lib" —— SDK 路径解析失败
#   本脚本完全不依赖 reg.exe：直接从文件系统探测最新 Windows SDK 与 VC 工具链，
#   注入 $env:LIB 并把 link.exe / lib.exe 路径显式传给 MSBuild。
param(
    [string]$Runtime = "win-x64",
    [string]$Config  = "Release"
)

$ErrorActionPreference = "Stop"

# 0) Git Bash / 非标准 shell 下 OS 变量可能缺失，会导致 ILC 误判为跨平台编译
if (-not $env:OS) { $env:OS = 'Windows_NT' }

# 1) 探测 Windows SDK（取版本号最高的）
$kitsRoot = "C:\Program Files (x86)\Windows Kits\10\Lib"
$sdkVer = Get-ChildItem $kitsRoot -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
    Sort-Object { [version]($_.Name) } -Descending |
    Select-Object -First 1
if (-not $sdkVer) { throw "未找到 Windows SDK Lib 目录: $kitsRoot" }

# 2) 探测 MSVC 工具链（取版本号最高的）
$vcTools = Get-ChildItem "C:\Program Files\Microsoft Visual Studio\*\*\VC\Tools\MSVC\*" -Directory -ErrorAction SilentlyContinue |
    Sort-Object { $_.Name } -Descending |
    Select-Object -First 1
if (-not $vcTools) { throw "未找到 VC 工具链目录" }
$vcBin = Join-Path $vcTools.FullName 'bin\Hostx64\x64'
if (-not (Test-Path (Join-Path $vcBin 'link.exe'))) { throw "未找到 link.exe: $vcBin" }

$libParts = @(
    (Join-Path $vcTools.FullName 'lib\x64')
    (Join-Path $sdkVer.FullName 'um\x64')
    (Join-Path $sdkVer.FullName 'ucrt\x64')
)
$env:LIB = $libParts -join ';'

Write-Host "SDK        : $($sdkVer.FullName)"
Write-Host "VC Tools   : $($vcTools.FullName)"
Write-Host "已注入 LIB :"
$libParts | ForEach-Object { Write-Host "  $_" }
Write-Host ""

dotnet publish src/Yuehua -c $Config -r $Runtime --self-contained `
    -p:IlcUseEnvironmentalTools=true `
    -p:CppLinker="$vcBin\link.exe" `
    -p:CppLibCreator="$vcBin\lib.exe"
if ($LASTEXITCODE -ne 0) { throw "publish 失败（exit $LASTEXITCODE）" }

# 3) 清理调试符号，发布目录从 ~240MB 降到 ~63MB
$pub = "src/Yuehua/bin/$Config/net10.0/$Runtime/publish"
Get-ChildItem $pub -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force

Write-Host ""
Write-Host "AOT 单目录产物位于："
Write-Host "  $pub/"
