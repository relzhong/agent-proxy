$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$testOutput = Join-Path ([IO.Path]::GetTempPath()) ('agentproxy-process-proxy-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testOutput | Out-Null
$compiler = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /reference:System.Web.Extensions.dll /reference:System.Windows.Forms.dll "/out:$testOutput\ProcessProxyTests.exe" "$repo\src\ProcessProxy\ProcessProxy.cs" "$repo\src\ProcessProxy\ProcessProxyController.cs" "$repo\src\ProcessProxy\ProcessProxySecurity.cs" "$repo\src\ProcessProxy\ProcessProxyBroker.cs" "$repo\src\ProcessProxy\ProcessProxyApproval.cs" "$repo\src\ProcessProxy\ProcessProxyArtifacts.cs" "$repo\src\ProcessProxy\ProcessProxyDiagnostic.cs" "$repo\tests\ProcessProxyTests.cs"
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
& "$testOutput\ProcessProxyTests.exe"
if ($LASTEXITCODE -ne 0) { throw 'Isolated tests failed.' }
Write-Host "Isolated outputs retained at $testOutput"
