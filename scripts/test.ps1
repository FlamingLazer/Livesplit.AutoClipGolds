param([Parameter(Mandatory = $true)][string]$LiveSplitPath)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$LiveSplitPath = (Resolve-Path -LiteralPath $LiveSplitPath).Path
& (Join-Path $PSScriptRoot 'build.ps1') -LiveSplitPath $LiveSplitPath
$testRoot = Join-Path $repoRoot 'work\GoldClip'
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
$ffmpeg = (Get-Command ffmpeg -ErrorAction Stop).Source
$python = (Get-Command python -ErrorAction Stop).Source
$probe = Join-Path (Split-Path $ffmpeg) 'ffprobe.exe'
if (!(Test-Path -LiteralPath $probe)) { throw 'FFprobe must be installed beside FFmpeg.' }
Copy-Item -LiteralPath (Join-Path $repoRoot 'dist\LiveSplit.GoldClip.dll') -Destination $testRoot -Force
foreach ($name in 'LiveSplit.Core.dll', 'UpdateManager.dll') { Copy-Item -LiteralPath (Join-Path $LiveSplitPath $name) -Destination $testRoot -Force }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
& $compiler /nologo /target:exe "/out:$testRoot\Tests.exe" "/reference:$testRoot\LiveSplit.GoldClip.dll" "/reference:$LiveSplitPath\LiveSplit.Core.dll" (Join-Path $repoRoot 'tests\Tests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
$oldLiveSplit = $env:AUTO_CLIP_LIVESPLIT
$mock = $null
Push-Location $repoRoot
try {
    $env:AUTO_CLIP_LIVESPLIT = $LiveSplitPath
    & $ffmpeg -nostdin -v error -y -f lavfi -i 'testsrc2=size=160x90:rate=30' -f lavfi -i 'sine=frequency=440:sample_rate=48000' -t 20 -c:v libx264 -g 60 -c:a aac (Join-Path $testRoot 'fixture2.mkv')
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the test video.' }
    Copy-Item -LiteralPath (Join-Path $testRoot 'fixture2.mkv') -Destination (Join-Path $testRoot 'fixture3.mkv') -Force
    Copy-Item -LiteralPath (Join-Path $testRoot 'fixture2.mkv') -Destination (Join-Path $testRoot 'fixture4.mkv') -Force
    $mock = Start-Process -FilePath $python -ArgumentList 'tests\mock_obs.py' -WorkingDirectory $repoRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $testRoot 'mock.log') -RedirectStandardError (Join-Path $testRoot 'mock-error.log')
    & (Join-Path $testRoot 'Tests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed. Check work\GoldClip for logs.' }
} finally {
    if ($mock -and !$mock.HasExited) { Stop-Process -Id $mock.Id -ErrorAction SilentlyContinue }
    $env:AUTO_CLIP_LIVESPLIT = $oldLiveSplit
    Pop-Location
}
