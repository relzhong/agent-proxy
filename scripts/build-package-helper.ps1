param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$outputDir = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $repo 'output\dist' }
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
$compiler = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$output = Join-Path $outputDir 'PackagedChatGPTLauncher.exe'
& $compiler /nologo /target:exe /optimize+ "/out:$output" `
    (Join-Path $repo 'src\Codex\PackagedChatGPTLauncher.cs') `
    (Join-Path $repo 'src\Codex\BrowserReadLaunchPolicy.cs') `
    (Join-Path $repo 'src\Codex\ActivationProcessResolver.cs')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Built: $output"
