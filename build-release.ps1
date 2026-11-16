#requires -Version 7.0
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
[xml]$projectMetadata = Get-Content -LiteralPath './src/VgnBattery.csproj' -Raw
$releaseVersion = [string]$projectMetadata.Project.PropertyGroup.Version
$targetFramework = [string]$projectMetadata.Project.PropertyGroup.TargetFramework
$publishDirectory = Join-Path $PSScriptRoot "artifacts/releases/VgnBattery-$releaseVersion-win-x64"
$releaseDirectory = Join-Path $PSScriptRoot 'releases'
# The ZIP already compresses the download. Leaving the embedded assemblies
# uncompressed avoids retaining their decompressed copies in process memory.
dotnet publish ./src/VgnBattery.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o $publishDirectory --nologo
if ($LASTEXITCODE -ne 0) { throw '发行版生成失败' }
$releaseExecutable = Join-Path $publishDirectory 'VgnBattery.exe'
$testDirectory = Join-Path $publishDirectory 'checks'
$testProcess = Start-Process -FilePath $releaseExecutable -ArgumentList @('--self-test','--output-dir',('"' + $testDirectory + '"')) -WindowStyle Hidden -Wait -PassThru
if ($testProcess.ExitCode -ne 0) { throw '发行版检查失败' }
$testResult = Get-Content -LiteralPath (Join-Path $testDirectory 'self-test.txt') -Raw
if ($testResult -notmatch '^Passed \d+ .*checks\.') { throw '发行版检查结果不符合预期' }

# Collect licenses from the exact runtime package versions used by this publish.
$runtimeConfig = Get-Content -LiteralPath (Join-Path $PSScriptRoot "src/bin/Release/$targetFramework/win-x64/VgnBattery.runtimeconfig.json") -Raw | ConvertFrom-Json
$coreRuntime = $runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -EQ 'Microsoft.NETCore.App'
$desktopRuntime = $runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -EQ 'Microsoft.WindowsDesktop.App'
if (-not $coreRuntime -or -not $desktopRuntime) { throw '无法确认自包含运行时版本' }
$packageRoot = (& dotnet msbuild ./src/VgnBattery.csproj -nologo -getProperty:NuGetPackageRoot).Trim()
if ($LASTEXITCODE -ne 0 -or -not $packageRoot) { throw '无法确认运行时包目录' }
$corePackage = Join-Path $packageRoot "microsoft.netcore.app.runtime.win-x64/$($coreRuntime.version)"
$desktopPackage = Join-Path $packageRoot "microsoft.windowsdesktop.app.runtime.win-x64/$($desktopRuntime.version)"
$packageFiles = @(
    @{ Source = $releaseExecutable; Entry = 'VgnBattery.exe' }
    @{ Source = (Join-Path $PSScriptRoot '发行说明.txt'); Entry = 'README.txt' }
    @{ Source = (Join-Path $PSScriptRoot 'LICENSE'); Entry = 'LICENSE' }
    @{ Source = (Join-Path $PSScriptRoot 'THIRD_PARTY_NOTICES.md'); Entry = 'THIRD_PARTY_NOTICES.md' }
    @{ Source = (Join-Path $corePackage 'LICENSE.TXT'); Entry = 'licenses/dotnet-runtime-LICENSE.txt' }
    @{ Source = (Join-Path $corePackage 'THIRD-PARTY-NOTICES.TXT'); Entry = 'licenses/dotnet-runtime-THIRD-PARTY-NOTICES.txt' }
    @{ Source = (Join-Path $desktopPackage 'LICENSE'); Entry = 'licenses/windowsdesktop-LICENSE.txt' }
)
foreach ($packageFile in $packageFiles) { if (-not (Test-Path -LiteralPath $packageFile.Source -PathType Leaf)) { throw "缺少发行文件：$($packageFile.Entry)" } }
New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
$archivePath = Join-Path $releaseDirectory "VgnBattery-$releaseVersion-win-x64.zip"
$archiveStream = [IO.File]::Open($archivePath,[IO.FileMode]::Create)
try {
    $archive = [IO.Compression.ZipArchive]::new($archiveStream,[IO.Compression.ZipArchiveMode]::Create,$true)
    try {
        foreach ($packageFile in $packageFiles) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$packageFile.Source,$packageFile.Entry,[IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $archive.Dispose() }
} finally { $archiveStream.Dispose() }
$archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $releaseDirectory 'SHA256SUMS.txt'), "$archiveHash  $([IO.Path]::GetFileName($archivePath))`n")
Write-Host $testResult.Trim()
Write-Host "Package: $archivePath ($([math]::Round((Get-Item -LiteralPath $archivePath).Length/1MB,1)) MiB)"
Write-Host "SHA256: $archiveHash"
