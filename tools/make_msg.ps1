# Outlook COM으로 .msg 테스트 픽스처를 만든다(Outlook Classic 필요, 메일은 발송하지 않음).
# 사용법: powershell -ExecutionPolicy Bypass -File tools\make_msg.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'tests\TechSupportReply.Tests\Fixtures\docs\reply.msg'

$outlook = New-Object -ComObject Outlook.Application
$mail = $outlook.CreateItem(0)   # olMailItem
$mail.Subject = 'RE: 메시 품질 문의'
$mail.Body = @"
안녕하세요, KOSTECH 기술지원팀입니다.

음수 부피 오류는 요소 왜곡이 원인이므로 메시 품질을 개선해 주세요.

-----Original Message-----
From: 김고객 <customer@example.com>
Sent: Monday, September 1, 2026 10:00 AM
Subject: 메시 품질 문의

negative volume 오류가 발생합니다.
"@
$mail.SaveAs($out, 3)            # olMSG
$mail.Close(1)                   # olDiscard
[System.Runtime.InteropServices.Marshal]::ReleaseComObject($mail) | Out-Null
Write-Output "wrote $out"
