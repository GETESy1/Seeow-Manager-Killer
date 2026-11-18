<#
.SYNOPSIS
    编译 / 发布 SeeowKiller。

.DESCRIPTION
    主界面是 WinUI 3（Windows App SDK），托盘/看门狗仍是 WinForms，两者在同一个 exe 里。

    三种产物：
      1) 普通编译            -> SeeowKiller\bin\...\SeeowKiller.exe（调试用）
      2) -Publish            -> dist\unpacked\ 文件夹（目标机需装 .NET 8 Desktop Runtime）
      3) -Publish -SingleFile -> dist\SeeowKiller-<版本>-win-x64-<flavor>.exe 单文件
           -SingleFile 默认「不带运行时」：目标机需装 .NET 8 Desktop Runtime
           -SingleFile -SelfContained：连 .NET 运行时一起打包，目标机什么都不用装
           -Compress 可再加单文件压缩（体积更小，首次启动稍慢）

    WinUI 3（Windows App SDK）以自包含方式随程序分发，目标机不需要另装 Windows App Runtime。
    单文件版首次运行会把内容解压到 %TEMP%\.net\SeeowKiller\...（.NET 自动缓存，之后启动很快）。

    构建需要 .NET 8 SDK（global.json 钉在 8.0.423，latestPatch 允许 8.0.4xx 更高补丁）。
    第一次构建会从 nuget.org 还原 Windows App SDK，之后可离线。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\build.ps1
    powershell -ExecutionPolicy Bypass -File .\build.ps1 -Publish
    powershell -ExecutionPolicy Bypass -File .\build.ps1 -Publish -SingleFile
    powershell -ExecutionPolicy Bypass -File .\build.ps1 -Publish -SingleFile -SelfContained
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$Publish,
    [switch]$SelfContained,
    [switch]$SingleFile,
    [switch]$Compress
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# 把 dotnet 的家目录/包目录放到仓库内：
# 一来避免往用户目录写东西（受限环境下会被拒绝），二来保持环境干净可复现。
$env:DOTNET_CLI_HOME = Join-Path $root '.dotnet-home'
$env:NUGET_PACKAGES = Join-Path $root '.nuget-packages'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

$project = Join-Path $root 'SeeowKiller\SeeowKiller.csproj'
if (-not (Test-Path $project)) { throw "找不到项目文件: $project" }

$tfm = 'net8.0-windows10.0.19041.0'

# 版本号只在 Program.cs 里维护一份，这里读出来给单文件命名用
$versionMatch = Select-String -Path (Join-Path $root 'SeeowKiller\Program.cs') -Pattern 'Version\s*=\s*"([0-9][0-9.]*)"'
$version = if ($versionMatch) { $versionMatch.Matches[0].Groups[1].Value } else { '0.0.0' }

if ($SingleFile) { $Publish = $true }

if ($SingleFile) {
    $flavor = if ($SelfContained) { 'selfcontained' } else { 'framework-dependent' }
    $staging = Join-Path $root '.publish\singlefile'
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue

    $dotnetArgs = @(
        'publish', $project, '-c', $Configuration, '-o', $staging, '--nologo',
        '-p:PublishSingleFile=true',
        '-p:IncludeAllContentForSelfExtract=true',
        "-p:SelfContained=$($SelfContained.IsPresent.ToString().ToLowerInvariant())"
    )
    if ($Compress) { $dotnetArgs += '-p:EnableCompressionInSingleFile=true' }

    Write-Host "dotnet $($dotnetArgs -join ' ')" -ForegroundColor Cyan
    & dotnet @dotnetArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $outDir = Join-Path $root 'dist'
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    $target = Join-Path $outDir "SeeowKiller-$version-win-x64-$flavor.exe"
    Copy-Item (Join-Path $staging 'SeeowKiller.exe') $target -Force
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue

    $sizeMb = [math]::Round((Get-Item $target).Length / 1MB, 1)
    Write-Host "`n单文件产物: $target（$sizeMb MB）" -ForegroundColor Green
    if ($SelfContained) {
        Write-Host "  目标机无需安装任何运行时。" -ForegroundColor Green
    } else {
        Write-Host "  目标机需要 .NET 8 Desktop Runtime。" -ForegroundColor Yellow
    }
}
elseif ($Publish) {
    $out = Join-Path $root 'dist\unpacked'
    $dotnetArgs = @('publish', $project, '-c', $Configuration, '-o', $out, '--nologo')
    if ($SelfContained) { $dotnetArgs += '-p:SelfContained=true' }
    Write-Host "dotnet $($dotnetArgs -join ' ')" -ForegroundColor Cyan
    & dotnet @dotnetArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Host "`n产物目录: $out" -ForegroundColor Green
}
else {
    Write-Host "dotnet build $project -c $Configuration" -ForegroundColor Cyan
    & dotnet build $project -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Host "`n产物: $root\SeeowKiller\bin\$Configuration\$tfm\win-x64\SeeowKiller.exe" -ForegroundColor Green
}

$nextSteps = @(
    '',
    '下一步：',
    '  1) 打开主界面（WinUI 3 设置窗口）:',
    '       .\SeeowKiller.exe --settings',
    '  2) 装自启（需要管理员，会弹一次 UAC）:',
    '       .\SeeowKiller.exe --install              # 登录时静默启动（推荐）',
    '       .\SeeowKiller.exe --install --boot       # 再加一个开机 SYSTEM 任务',
    '  3) 立刻试一次:',
    '       .\SeeowKiller.exe --status',
    '       .\SeeowKiller.exe --once',
    '  4) 卸载自启:',
    '       .\SeeowKiller.exe --uninstall',
    '',
    '注意：本程序是 GUI 子系统的 exe，PowerShell 里直接跑不会等它，',
    '      要看输出请用  .\SeeowKiller.exe --status > out.txt  然后读文件。'
)
Write-Host ($nextSteps -join [Environment]::NewLine) -ForegroundColor Yellow
