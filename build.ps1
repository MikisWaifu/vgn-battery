#requires -Version 7.0
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
dotnet publish ./src/VgnBattery.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o ./dist --nologo
if ($LASTEXITCODE -ne 0) { throw '生成失败' }
Write-Host "生成完成：$PSScriptRoot\dist\VgnBattery.exe"
