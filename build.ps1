# 사용법 (.NET 8 SDK 필요: dotnet 이 PATH 에 없으면 ~\.dotnet 을 사용합니다)
#   .\build.ps1                    전체 빌드 (허브 + 모든 모듈)
#   .\build.ps1 -Only PowerPoint   해당 모듈만 다시 빌드 -> modules\PowerPoint\ 만 갱신
#   .\build.ps1 -Only Core         허브(ToolHub.exe) 만 다시 빌드
# 실행 중인 파일은 덮어쓸 수 없으므로 빌드 전에 ToolHub 를 종료하세요.
#   .\build.ps1 -Out C:\경로        다른 폴더로 빌드 (실행 중인 ToolHub 를 건드리지 않고 시험할 때)
#   .\build.ps1 -SelfContained     .NET runtime bundled build -> dist\ToolHub\ + dist\ToolHub-win-x64.zip (no .NET install needed on target PC)
param([string]$Only = '', [string]$Out = '', [switch]$SelfContained)
$ErrorActionPreference = 'Stop'

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue)
$userDotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (Test-Path $userDotnet) { $dotnetPath = $userDotnet }
elseif ($dotnet) { $dotnetPath = $dotnet.Source }
else { throw '.NET 8 SDK 를 찾을 수 없습니다.' }
if (-not (& $dotnetPath --list-sdks | Select-String '^8\.')) { throw '.NET 8 SDK 가 설치되어 있지 않습니다.' }

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$root = $PSScriptRoot

if ($SelfContained -and $Out -eq '') { $Out = Join-Path $root 'dist\ToolHub' }
if ($SelfContained -and $Only -eq '' -and (Test-Path $Out)) { Remove-Item $Out -Recurse -Force }

function Build-Project([string]$csproj, [string]$label, [switch]$Publish) {
    $extra = @()
    if ($Out -ne '') { $extra += ('-p:ToolHubOut=' + $Out.TrimEnd('\') + '\') }
    if ($Publish) {
        # Only the hub exe is published with the runtime; modules load into the hub process, so a normal build is enough.
        $pubDir = $Out.TrimEnd('\') + '\'
        & $dotnetPath publish $csproj -c Release -r win-x64 --self-contained true -v quiet --nologo -clp:ErrorsOnly `
            -p:PublishDir=$pubDir -p:DebugType=None -p:DebugSymbols=false @extra
    } else {
        & $dotnetPath build $csproj -c Release -v quiet --nologo -clp:ErrorsOnly @extra
    }
    if ($LASTEXITCODE -ne 0) { throw "$label 빌드 실패" }
    Write-Host "OK  $label"
}

if ($Only -eq '' -or $Only -eq 'Core') {
    Build-Project (Join-Path $root 'src\Core\ToolHub.csproj') 'ToolHub.exe' -Publish:($SelfContained -and $Only -eq 'Core')
}
if ($Only -eq 'Core') { return }

Get-ChildItem (Join-Path $root 'src\Modules') -Directory | ForEach-Object {
    if ($Only -eq '' -or $Only -eq $_.Name) {
        Build-Project (Join-Path $_.FullName ($_.Name + '.csproj')) ("modules\" + $_.Name)
    }
}

if ($SelfContained -and $Only -eq '') {
    # Publish the hub last: building a module re-builds Core (framework-dependent) into the same folder and would
    # overwrite the self-contained ToolHub.deps.json / runtimeconfig.json.
    Build-Project (Join-Path $root 'src\Core\ToolHub.csproj') 'ToolHub.exe (self-contained)' -Publish
    Copy-Item (Join-Path $root 'README.md') $Out -Force
    $zip = Join-Path (Split-Path $Out -Parent) 'ToolHub-win-x64.zip'
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $Out '*') -DestinationPath $zip
    Write-Host "OK  $zip"
}
