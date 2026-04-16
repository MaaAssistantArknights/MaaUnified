param(
    [Parameter(Mandatory = $true)]
    [string]$TargetDir
)

New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null

$content = @'
@echo off
set "SCRIPT_DIR=%~dp0"
"%SCRIPT_DIR%bin\MAAUnified.exe" %*
'@

Set-Content -Path (Join-Path $TargetDir 'MAAUnified.cmd') -Value $content
