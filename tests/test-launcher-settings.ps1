$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$testOutput = Join-Path ([IO.Path]::GetTempPath()) ('agentproxy-settings-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testOutput | Out-Null
$compiler = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /platform:x64 /reference:System.Security.dll /reference:System.Web.Extensions.dll "/out:$testOutput\LauncherSettingsTests.exe" "$repo\src\Launcher\LauncherSettings.cs" "$repo\tests\LauncherSettingsTests.cs"
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
& "$testOutput\LauncherSettingsTests.exe"
if ($LASTEXITCODE -ne 0) { throw 'LauncherSettings tests failed.' }
Remove-Item -LiteralPath $testOutput -Recurse -Force -ErrorAction SilentlyContinue
