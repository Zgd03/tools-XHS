param(
    [ValidateSet('Portable','Lite','Both')][string]$Edition = 'Portable',
    [string]$DotnetPath,
    [switch]$NoRestore,
    [ValidatePattern('^[_a-zA-Z0-9.]*$')][string]$OutputSuffix = ''
)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
if (-not $DotnetPath) {
    $localSdk = Join-Path $env:LOCALAPPDATA 'DarenDev\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $localSdk) { $DotnetPath = $localSdk }
    else { $DotnetPath = (Get-Command dotnet -ErrorAction Stop).Source }
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$projectFile = Join-Path $projectRoot 'src\DarenTool\DarenTool.csproj'
function Publish-Edition([string]$Name, [bool]$SelfContained) {
    $destination = Join-Path $projectRoot "dist\$Name$OutputSuffix"
    $containedValue = $SelfContained.ToString().ToLowerInvariant()
    $restoreArguments = @()
    if ($NoRestore) { $restoreArguments += '--no-restore' }
    & $DotnetPath publish $projectFile -c Release -r win-x64 --self-contained $containedValue `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=$containedValue -p:DebugType=none -p:DebugSymbols=false -o $destination @restoreArguments
    if ($LASTEXITCODE -ne 0) { throw "发布 $Name 失败。" }
    Copy-Item -LiteralPath (Join-Path $projectRoot '开始使用.txt') -Destination $destination -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot '第三方声明.txt') -Destination $destination -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot '第三方许可.txt') -Destination $destination -Force
    $executable = Join-Path $destination 'DarenTool.exe'
    Get-FileHash -LiteralPath $executable -Algorithm SHA256 | ForEach-Object { "$($_.Hash)  DarenTool.exe" } |
        Set-Content -LiteralPath (Join-Path $destination 'SHA256.txt') -Encoding utf8
    Write-Host "已发布：$executable"
}
if ($Edition -in @('Portable','Both')) { Publish-Edition '便携版' $true }
if ($Edition -in @('Lite','Both')) { Publish-Edition '精简版' $false }
Write-Host '构建完成。未启动应用、未运行测试。'
