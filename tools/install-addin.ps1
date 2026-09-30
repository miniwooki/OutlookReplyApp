<#
.SYNOPSIS
  빌드 폴더를 사용자 폴더로 복사하고 Outlook 애드인으로 등록한다(방법 B, 관리자 권한 불필요).
.EXAMPLE
  .\install-addin.ps1 -Source C:\Deploy\TechSupportReply\bin
  .\install-addin.ps1 -Uninstall
#>
param(
    [string]$Source,
    [string]$Target = (Join-Path $env:LOCALAPPDATA 'Programs\TechSupportReply'),
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
$Target = $Target.TrimEnd([char]92, '/')
if ($Source) { $Source = $Source.TrimEnd([char]92, '/') }
$name = 'TechSupportReply.AddIn'
$addinKey = "HKCU:\Software\Microsoft\Office\Outlook\Addins\$name"
$inclusionRoot = 'HKCU:\Software\Microsoft\VSTO\Security\Inclusion'
$manifestUrl = ([Uri](Join-Path $Target "$name.vsto")).AbsoluteUri

if (Get-Process OUTLOOK -ErrorAction SilentlyContinue) { throw 'Outlook을 종료한 뒤 다시 실행하세요.' }

# 이전 신뢰 목록 항목 제거(같은 URL)
if (Test-Path $inclusionRoot) {
    Get-ChildItem $inclusionRoot | Where-Object { (Get-ItemProperty $_.PSPath).Url -eq $manifestUrl } | Remove-Item -Recurse -Force
}

if ($Uninstall) {
    if (Test-Path $addinKey) {
        # 이 설치가 등록한 키만 지운다(개발 빌드 등 다른 등록은 그대로 둔다).
        $current = (Get-ItemProperty $addinKey).Manifest
        if ($current -and $current.StartsWith($manifestUrl, [StringComparison]::OrdinalIgnoreCase)) { Remove-Item $addinKey -Recurse -Force }
        else { Write-Host "Outlook 애드인 등록이 이 설치를 가리키지 않아 그대로 두었습니다: $current" }
    }
    if (Test-Path $Target) { Remove-Item $Target -Recurse -Force }
    Write-Host '제거했습니다. 설정(%APPDATA%\TechSupportReply)과 캐시(%LOCALAPPDATA%\TechSupportReply)는 남겨 두었습니다.'
    return
}

if (-not $Source) { throw '-Source(빌드 폴더)를 지정하세요.' }
if (-not (Test-Path (Join-Path $Source "$name.vsto"))) { throw "$Source 에 $name.vsto 가 없습니다." }

New-Item -ItemType Directory -Force $Target | Out-Null
robocopy "$Source" "$Target" /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "파일 복사 실패(robocopy $LASTEXITCODE)" }

# 서명 인증서 공개 키로 VSTO 신뢰 목록에 추가(설치 확인 창 없이 로드)
[xml]$vsto = Get-Content (Join-Path $Target "$name.vsto") -Raw
$ns = New-Object Xml.XmlNamespaceManager $vsto.NameTable
$ns.AddNamespace('ds', 'http://www.w3.org/2000/09/xmldsig#')
$certNode = $vsto.SelectSingleNode('//ds:X509Certificate', $ns)
if (-not $certNode) { throw '.vsto 매니페스트에서 서명 인증서를 찾을 수 없습니다.' }
$cert = New-Object Security.Cryptography.X509Certificates.X509Certificate2 (,[Convert]::FromBase64String($certNode.InnerText))
$publicKey = $cert.PublicKey.Key.ToXmlString($false)
$entry = Join-Path $inclusionRoot ([guid]::NewGuid().ToString())
New-Item -Path $entry -Force | Out-Null
New-ItemProperty -Path $entry -Name Url -Value $manifestUrl -PropertyType String -Force | Out-Null
New-ItemProperty -Path $entry -Name PublicKey -Value $publicKey -PropertyType String -Force | Out-Null

New-Item -Path $addinKey -Force | Out-Null
New-ItemProperty -Path $addinKey -Name FriendlyName -Value '기술지원 답변 도우미' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $addinKey -Name Description -Value 'LS-DYNA·Ansys 기술지원 메일 회신 초안(자동 발송 없음)' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $addinKey -Name LoadBehavior -Value 3 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $addinKey -Name Manifest -Value "$manifestUrl|vstolocal" -PropertyType String -Force | Out-Null

Write-Host "설치했습니다: $Target"
Write-Host 'Outlook을 시작하면 [홈] 탭에 "기술지원" 그룹이 나타납니다.'
