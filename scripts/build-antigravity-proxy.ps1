param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
. "$PSScriptRoot\msvc-environment.ps1"
if(!$OutputDirectory){$OutputDirectory=Join-Path $repo 'output\antigravity-proxy'}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$core=Join-Path $repo 'native\antigravity-proxy'
$srcRoot=Join-Path $core 'src'
$compile=Join-Path ([IO.Path]::GetTempPath()) ('agp-build-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $compile | Out-Null
$sources=@(
    'buffer.c',
    'hook.c',
    'trampoline.c',
    'hde\hde64.c',
    'main.cpp',
    'hooks\Hooks.cpp',
    'proxy\VersionProxy.cpp',
    'update\UpdateChecker.cpp',
    'ide\IdeExtensionPrompt.cpp'
) | ForEach-Object { Join-Path $srcRoot $_ }
$outDll=Join-Path $OutputDirectory 'version.dll'
Push-Location $compile
try{
    & cl.exe /nologo /utf-8 /EHsc /std:c++17 /O2 /MT /GS /guard:cf /DNOMINMAX /DNDEBUG "/I$core\include" "/I$srcRoot" "/I$srcRoot\hde" @sources /LD /link /DYNAMICBASE /HIGHENTROPYVA /NXCOMPAT /guard:cf /CETCOMPAT "/DEF:$srcRoot\proxy\version.def" ws2_32.lib winhttp.lib "/OUT:$outDll"
    if($LASTEXITCODE -ne 0){throw 'Antigravity proxy version.dll compilation failed.'}
}finally{
    Pop-Location
    Remove-Item -LiteralPath $compile -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host "Built: $outDll"
