$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$testOutput = Join-Path ([IO.Path]::GetTempPath()) ('agentproxy-antigravity-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testOutput | Out-Null
$compiler = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /platform:x64 /reference:System.Web.Extensions.dll "/out:$testOutput\AntigravityLauncherTests.exe" "$repo\src\Antigravity\AntigravityLauncher.cs" "$repo\src\ProcessProxy\ProcessProxy.cs" "$repo\src\ProcessProxy\ProcessProxySecurity.cs" "$repo\src\ProcessProxy\ProcessProxyArtifacts.cs" "$repo\src\ProcessProxy\ProcessProxyDiagnostic.cs" "$repo\tests\AntigravityLauncherTests.cs"
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
& "$testOutput\AntigravityLauncherTests.exe"
if ($LASTEXITCODE -ne 0) { throw 'Antigravity launcher tests failed.' }
Remove-Item -LiteralPath $testOutput -Recurse -Force -ErrorAction SilentlyContinue
