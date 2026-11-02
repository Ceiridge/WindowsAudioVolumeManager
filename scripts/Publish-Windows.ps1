[CmdletBinding()]
param([string]$Dotnet = 'dotnet')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Run this validation/publish script on Windows.' }
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
[IO.Directory]::CreateDirectory($artifacts) | Out-Null
$utf8 = New-Object Text.UTF8Encoding($false)
$version = '2.0.0-device-volumes-preview.3'
$started = [DateTime]::UtcNow.ToString('o')
# Only input files are included: never build outputs, VCS data or local profiles.
$sourceManifest = [ordered]@{}
foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File -Force) {
    $relative = $file.FullName.Substring($root.Length + 1).Replace('\','/')
    if ($relative -match '(^|/)(bin|obj|artifacts|\.git)(/|$)') { continue }
    $sourceManifest[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
[IO.File]::WriteAllText((Join-Path $artifacts 'source-manifest.json'), ($sourceManifest | ConvertTo-Json -Depth 5), $utf8)
$reportPath = Join-Path $artifacts 'build-result.json'
# A prior success report must never mask the failure of a subsequent build.
[IO.File]::WriteAllText($reportPath, '{"succeeded":false,"status":"running"}', $utf8)

function Invoke-DotnetChecked([string[]]$Arguments, [string]$LogName) {
    $previous = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $Dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $artifacts $LogName) | Out-Host
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $previous }
    if ($code -ne 0) { throw ".NET command failed with exit code $code. See $LogName." }
}

Push-Location $root
try {
    Invoke-DotnetChecked @('build', 'WindowsAudioVolumeManager.csproj', '-c', 'Release',
        '-p:DebugType=none', '-p:DebugSymbols=false', ('-p:Version=' + $version)) 'build.log'
    Invoke-DotnetChecked @('run', '--project', 'tests/VolumeRules.Tests/VolumeRules.Tests.csproj',
        '-c', 'Release', '--', '--report', (Join-Path $artifacts 'unit-results.json')) 'tests.log'
    $unit = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $artifacts 'unit-results.json') | ConvertFrom-Json
    if ($unit.failed -ne 0 -or $unit.tests -lt 58) { throw 'The unit test report does not show a successful test run.' }
    $publish = Join-Path $artifacts 'publish\win-x64'
    if (Test-Path -LiteralPath $publish) { throw 'A publish directory already exists. Move it aside before rebuilding; no files were deleted.' }
    Invoke-DotnetChecked @('publish', 'WindowsAudioVolumeManager.csproj', '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:PublishTrimmed=false', '-p:DebugType=none', '-p:DebugSymbols=false', ('-p:Version=' + $version),
        ('-p:PathMap=' + $root + '=/_/'), '-o', $publish) 'publish.log'
    $exe = Join-Path $publish 'WindowsAudioVolumeManager.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Publish did not produce the expected executable.' }
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $publish 'LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $root 'docs\DEVICE_VOLUMES.md') -Destination (Join-Path $publish 'DEVICE_VOLUMES.md')
    $hashes = @(Get-ChildItem -LiteralPath $publish -Recurse -File | ForEach-Object {
        (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.FullName.Substring($publish.Length + 1).Replace('\','/')
    })
    [IO.File]::WriteAllLines((Join-Path $publish 'SHA256SUMS.txt'), $hashes, $utf8)
    $package = Join-Path $artifacts ('WindowsAudioVolumeManager-' + $version + '-win-x64.zip')
    [IO.Compression.ZipFile]::CreateFromDirectory($publish, $package, [IO.Compression.CompressionLevel]::Optimal, $false)
    $result = [ordered]@{
        succeeded = $true; version = $version; startedUtc = $started; completedUtc = [DateTime]::UtcNow.ToString('o')
        sdk = (& $Dotnet --version | Out-String).Trim(); tests = $unit.tests; testsPassed = $unit.passed
        baseCommit = '332ab6fbcc06adc2af514f3cc600029edc53c722'
        exeSha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
        actualWindowsAudioTested = $false; executableLaunched = $false
    }
    [IO.File]::WriteAllText($reportPath, ($result | ConvertTo-Json -Depth 8), $utf8)
    Write-Host "Built $package. Real audio and UI acceptance remain pending. Nothing was launched."
} catch {
    $failure = @{ succeeded = $false; status = 'failed'; message = $_.Exception.Message }
    [IO.File]::WriteAllText($reportPath, ($failure | ConvertTo-Json), $utf8)
    throw
} finally { Pop-Location }
