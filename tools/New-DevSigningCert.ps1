<#
.SYNOPSIS
  VSTO 매니페스트 서명용 코드 서명 인증서를 준비하고 src/TechSupportReply.AddIn/Signing.user.props에 지문을 기록한다.
.DESCRIPTION
  -PfxPath를 주면 회사 인증서(PFX)를 현재 사용자 저장소로 가져와 사용한다. 주지 않으면 개발용 자체 서명 인증서를 재사용하거나 새로 만든다.
  공개 인증서(.cer)는 publish\TechSupportReply-signing.cer로 내보낸다(팀 PC 신뢰 배포용).
#>
param(
    [string]$Subject = 'CN=TechSupportReply Dev (KOSTECH)',
    [string]$PfxPath,
    [securestring]$PfxPassword
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

if ($PfxPath) {
    $cert = Import-PfxCertificate -FilePath $PfxPath -CertStoreLocation Cert:\CurrentUser\My -Password $PfxPassword -Exportable
} else {
    $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
        Where-Object { $_.Subject -eq $Subject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $cert) {
        $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Subject -CertStoreLocation Cert:\CurrentUser\My `
            -NotAfter (Get-Date).AddYears(5) -KeyExportPolicy Exportable -HashAlgorithm SHA256
        Write-Host "새 개발용 인증서를 만들었습니다: $($cert.Thumbprint)"
    }
}

$props = Join-Path $repo 'src\TechSupportReply.AddIn\Signing.user.props'
@"
<Project>
  <PropertyGroup>
    <ManifestCertificateThumbprint>$($cert.Thumbprint)</ManifestCertificateThumbprint>
  </PropertyGroup>
</Project>
"@ | Set-Content -Path $props -Encoding UTF8

$publish = Join-Path $repo 'publish'
New-Item -ItemType Directory -Force $publish | Out-Null
Export-Certificate -Cert $cert -FilePath (Join-Path $publish 'TechSupportReply-signing.cer') | Out-Null
Write-Host "서명 인증서: $($cert.Subject) / $($cert.Thumbprint)"
Write-Host "기록: $props"
