param([string]$OutputDirectory, [string]$NativeArtifactsSource, [switch]$SkipAntigravityNative)
$ErrorActionPreference = 'Stop'
$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$outputDir = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $projectDir 'output\dist' }
$artifactsSource = if ($NativeArtifactsSource) { [IO.Path]::GetFullPath($NativeArtifactsSource) } else { Join-Path $projectDir 'src\ProcessProxy\ProcessProxyArtifacts.cs' }
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

& (Join-Path $projectDir 'scripts\build-package-helper.ps1') -OutputDirectory $outputDir
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$windowsDir = Split-Path -Parent ([Environment]::SystemDirectory)
$fxDir = "$windowsDir\Microsoft.NET\Framework64\v4.0.30319"
$csc = "$fxDir\csc.exe"
$launcherExe = Join-Path $outputDir 'AgentProxyLauncher.exe'

& $csc `
    /nologo /target:winexe /platform:x64 /optimize+ `
    /win32icon:"$projectDir\assets\app-icon.ico" `
    /win32manifest:"$projectDir\assets\app.manifest" `
    /resource:"$projectDir\assets\app-icon.png,app-icon.png" `
    /out:"$launcherExe" `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll `
    "/reference:$fxDir\System.Xaml.dll" `
    "/reference:$fxDir\WPF\PresentationFramework.dll" `
    "/reference:$fxDir\WPF\PresentationCore.dll" `
    "/reference:$fxDir\WPF\WindowsBase.dll" `
    "$projectDir\src\Launcher\Program.cs" `
    "$projectDir\src\Launcher\LauncherDiagnostics.cs" `
    "$projectDir\src\Launcher\PortableRuntime.cs" `
    "$projectDir\src\Antigravity\AntigravityLauncher.cs" `
    "$projectDir\src\Codex\CuaProxyForwardingMaintenance.cs" `
    "$projectDir\src\Codex\CuaProxyMaintenanceDialog.cs" `
    "$projectDir\src\ProcessProxy\ProcessProxy.cs" `
    "$projectDir\src\ProcessProxy\ProcessProxyController.cs" `
    "$projectDir\src\ProcessProxy\ProcessProxySecurity.cs" `
    $artifactsSource `
    "$projectDir\src\ProcessProxy\ProcessProxyDiagnostic.cs"

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if (-not $SkipAntigravityNative) {
    & (Join-Path $projectDir 'scripts\build-antigravity-proxy.ps1') -OutputDirectory (Join-Path $outputDir 'antigravity-proxy')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$trustedClientHash = (Get-FileHash -LiteralPath $launcherExe -Algorithm SHA256).Hash
$trustedClientSource = Join-Path ([IO.Path]::GetTempPath()) ('agentproxy-trusted-client-' + [Guid]::NewGuid().ToString('N') + '.cs')
('namespace AgentProxy.ProcessProxy { internal static class TrustedClient { public const string Sha256="' + $trustedClientHash + '"; } }') | Set-Content -LiteralPath $trustedClientSource -Encoding UTF8
try {
    & $csc /nologo /target:winexe /platform:x64 /optimize+ /reference:System.Web.Extensions.dll /reference:System.Windows.Forms.dll `
        "/win32manifest:$projectDir\assets\app.manifest" `
        "/out:$outputDir\ProcessProxyHost.exe" `
        "$projectDir\src\ProcessProxy\ProcessProxy.cs" `
        "$projectDir\src\ProcessProxy\ProcessProxyHost.cs" `
        "$projectDir\src\ProcessProxy\ProcessProxyBroker.cs" `
        "$projectDir\src\ProcessProxy\ProcessProxyApproval.cs" `
        "$projectDir\src\ProcessProxy\ProcessProxySecurity.cs" `
        $artifactsSource `
        "$projectDir\src\ProcessProxy\ProcessProxyDiagnostic.cs" `
        $trustedClientSource
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} finally { Remove-Item -LiteralPath $trustedClientSource -ErrorAction SilentlyContinue }

Write-Host "Built: $launcherExe"
