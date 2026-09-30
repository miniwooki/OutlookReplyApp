<#
.SYNOPSIS
  ClickOnce로 애드인을 게시한다(방법 A). 팀원은 게시 폴더의 TechSupportReply.AddIn.vsto를 실행해 설치하고, 이후 Outlook 시작 시 자동 업데이트된다.
.EXAMPLE
  .\publish-addin.ps1 -PublishDir \\server\deploy\TechSupportReply
#>
param(
    [Parameter(Mandatory)][string]$PublishDir,
    [string]$InstallUrl = $PublishDir
)
$ErrorActionPreference = 'Stop'
# 끝의 경로 구분자를 제거한다(따옴표 안에서 \" 로 해석되어 명령줄 인수가 깨지는 것을 막는다).
$PublishDir = $PublishDir.TrimEnd([char]92, '/')
$InstallUrl = $InstallUrl.TrimEnd([char]92, '/')
$repo = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $repo 'src\TechSupportReply.AddIn\TechSupportReply.AddIn.csproj'
. (Join-Path $PSScriptRoot '_msbuild.ps1')
$msbuild = Get-TsrMsBuild
Initialize-TsrSigning -Repo $repo

# 게시할 때마다 수정 번호를 올린다(ClickOnce 업데이트 판단 기준)
$revFile = Join-Path $repo 'publish\revision.txt'
New-Item -ItemType Directory -Force (Split-Path $revFile) | Out-Null
$rev = if (Test-Path $revFile) { [int](Get-Content $revFile) + 1 } else { 1 }
Set-Content $revFile $rev

& $msbuild $proj -restore -nologo -v:m -t:Publish -p:Configuration=Release "-p:ApplicationRevision=$rev" "-p:InstallUrl=$InstallUrl" "-p:PublishUrl=$PublishDir"
if ($LASTEXITCODE -ne 0) { throw "게시 빌드 실패(exit $LASTEXITCODE)" }

$appPublish = Join-Path $repo 'src\TechSupportReply.AddIn\bin\Release\app.publish'
if (-not (Test-Path (Join-Path $appPublish 'TechSupportReply.AddIn.vsto'))) { throw "게시 결과가 없습니다: $appPublish" }
New-Item -ItemType Directory -Force $PublishDir | Out-Null
robocopy "$appPublish" "$PublishDir" /E /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "게시 폴더 복사 실패(robocopy $LASTEXITCODE)" }
Copy-Item (Join-Path $repo 'publish\TechSupportReply-signing.cer') $PublishDir -ErrorAction SilentlyContinue
Write-Host "게시 완료(수정 $rev): $PublishDir"
