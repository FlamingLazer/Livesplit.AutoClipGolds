param([Parameter(Mandatory = $true)][string]$LiveSplitPath)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$LiveSplitPath = (Resolve-Path -LiteralPath $LiveSplitPath).Path
foreach ($name in 'LiveSplit.Core.dll', 'UpdateManager.dll') {
    if (!(Test-Path -LiteralPath (Join-Path $LiveSplitPath $name))) { throw "Missing $name. Supply the folder containing LiveSplit.exe." }
}
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'The .NET Framework C# compiler was not found.' }
$dist = Join-Path $repoRoot 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
& $compiler /nologo /target:library "/out:$dist\LiveSplit.GoldClip.dll" "/reference:$LiveSplitPath\LiveSplit.Core.dll" "/reference:$LiveSplitPath\UpdateManager.dll" /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:System.Security.dll "/reference:$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\Microsoft.VisualBasic.dll" (Join-Path $repoRoot 'src\GoldClip.cs')
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Output "Built $dist\LiveSplit.GoldClip.dll"
