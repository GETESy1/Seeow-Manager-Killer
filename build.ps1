<#
.SYNOPSIS
    编译 / 发布 SeeowKiller。

.DESCRIPTION
    默认框架依赖编译（产物小，目标机需装 .NET 8 Desktop Runtime）。
    加 -SelfContained 会产出单文件自包含 exe（约 150MB，目标机无需装运行时），
    这一步需要联网拉 runtime pack。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\build.ps1
    powershell -ExecutionPolicy Bypass -File .\build.ps1 -Publish -SelfContained
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$Publish,
    [switch]$SelfContained
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

if ($Publish) {
    $out = Join-Path $root 'dist'
    $dotnetArgs = @('publish', $project, '-c', $Configuration, '-o', $out, '--nologo')
    if ($SelfContained) {
        $dotnetArgs += @('-r', 'win-x64', '-p:SelfContained=true', '-p:PublishSingleFile=true')
    }
    Write-Host "dotnet $($dotnetArgs -join ' ')" -ForegroundColor Cyan
    & dotnet @dotnetArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Host "`n产物目录: $out" -ForegroundColor Green
}
else {
    Write-Host "dotnet build $project -c $Configuration" -ForegroundColor Cyan
    & dotnet build $project -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Host "`n产物: $root\SeeowKiller\bin\$Configuration\net8.0-windows\SeeowKiller.exe" -ForegroundColor Green
}

$nextSteps = @(
    '',
    '下一步：',
    '  1) 装自启（需要管理员，会弹一次 UAC）:',
    '       .\SeeowKiller.exe --install              # 登录时静默启动（推荐）',
    '       .\SeeowKiller.exe --install --boot       # 再加一个开机 SYSTEM 任务',
    '  2) 立刻试一次:',
    '       .\SeeowKiller.exe --status',
    '       .\SeeowKiller.exe --once',
    '  3) 卸载自启:',
    '       .\SeeowKiller.exe --uninstall',
    '',
    '注意：本程序是 GUI 子系统的 exe，PowerShell 里直接跑不会等它，',
    '      要看输出请用  .\SeeowKiller.exe --status > out.txt  然后读文件。'
)
Write-Host ($nextSteps -join [Environment]::NewLine) -ForegroundColor Yellow
