<#
.SYNOPSIS
  build-addin.ps1 / publish-addin.ps1 이 dot-source 하는 공용 함수(MSBuild 탐색, 서명 인증서 준비).
#>

# "Office/SharePoint 개발" 워크로드가 설치된 Visual Studio의 MSBuild.exe 경로를 돌려준다.
function Get-TsrMsBuild {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vswhere)) { throw "vswhere.exe를 찾을 수 없습니다($vswhere). Visual Studio 2022(Office/SharePoint 개발 워크로드)를 설치하세요." }
    $msbuild = & $vswhere -latest -requires Microsoft.VisualStudio.Workload.Office -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    if (-not $msbuild) { throw '"Office/SharePoint 개발" 워크로드가 설치된 Visual Studio를 찾을 수 없습니다.' }
    return $msbuild
}

# Signing.user.props 가 없으면 New-DevSigningCert.ps1 로 서명 인증서를 준비한다.
function Initialize-TsrSigning {
    param([Parameter(Mandatory)][string]$Repo)
    if (-not (Test-Path (Join-Path $Repo 'src\TechSupportReply.AddIn\Signing.user.props'))) {
        & (Join-Path $PSScriptRoot 'New-DevSigningCert.ps1')
    }
}
