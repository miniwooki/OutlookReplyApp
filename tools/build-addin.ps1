<#
.SYNOPSIS
  VSTO 애드인을 VS 2022 MSBuild로 빌드한다(dotnet build로는 VSTO 대상 파일을 쓸 수 없다).
  Debug 빌드는 개발자 PC의 Outlook에 애드인을 자동 등록한다.
#>
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -requires Microsoft.VisualStudio.Workload.Office -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw '"Office/SharePoint 개발" 워크로드가 설치된 Visual Studio를 찾을 수 없습니다.' }
if (-not (Test-Path (Join-Path $repo 'src\TechSupportReply.AddIn\Signing.user.props'))) { & (Join-Path $PSScriptRoot 'New-DevSigningCert.ps1') }
& $msbuild (Join-Path $repo 'src\TechSupportReply.AddIn\TechSupportReply.AddIn.csproj') -restore -nologo -v:m -clp:Summary "-p:Configuration=$Configuration"
if ($LASTEXITCODE -ne 0) { throw "빌드 실패(exit $LASTEXITCODE)" }

# 산출물 확인. net48은 e_sqlite3.dll을 runtimes\win-x64\native에 두므로 두 위치를 모두 인정한다(NativeLibraryPreloader도 두 곳을 찾는다).
$out = Join-Path $repo "src\TechSupportReply.AddIn\bin\$Configuration"
$required = @{
    'TechSupportReply.AddIn.vsto'         = @('TechSupportReply.AddIn.vsto')
    'TechSupportReply.AddIn.dll.manifest' = @('TechSupportReply.AddIn.dll.manifest')
    'TechSupportReply.AddIn.dll.config'   = @('TechSupportReply.AddIn.dll.config')
    'TechSupportReply.App.dll'            = @('TechSupportReply.App.dll')
    'onnxruntime.dll'                     = @('onnxruntime.dll', 'runtimes\win-x64\native\onnxruntime.dll')
    'e_sqlite3.dll'                       = @('e_sqlite3.dll', 'runtimes\win-x64\native\e_sqlite3.dll')
}
$missing = @()
foreach ($name in $required.Keys | Sort-Object) {
    $found = $required[$name] | Where-Object { Test-Path (Join-Path $out $_) } | Select-Object -First 1
    if ($found) { Write-Host ("  {0}: {1}" -f $name, $found) } else { Write-Host ("  {0}: 없음" -f $name); $missing += $name }
}
if ($missing.Count -gt 0) { throw "산출물이 없습니다: $($missing -join ', ')" }
Write-Host "빌드 완료: $out"
