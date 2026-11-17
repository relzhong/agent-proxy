param([Parameter(Mandatory=$true)][string]$LauncherExecutable,[string]$PreviewPath)
$ErrorActionPreference='Stop'
$testOutput=Join-Path ([IO.Path]::GetTempPath()) ('agentproxy-window-lifetime-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testOutput)|Out-Null
$fxDir=Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework64\v4.0.30319'
$compiler=Join-Path $fxDir 'csc.exe'
& $compiler /nologo /platform:x64 /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "/reference:$fxDir\System.Xaml.dll" "/reference:$fxDir\WPF\PresentationFramework.dll" "/reference:$fxDir\WPF\PresentationCore.dll" "/reference:$fxDir\WPF\WindowsBase.dll" "/out:$testOutput\WindowLifetimeTests.exe" "$PSScriptRoot\LauncherWindowLifetimeTests.cs"
if($LASTEXITCODE -ne 0){throw 'Lifetime test compilation failed.'}
& $compiler /nologo "/out:$testOutput\PackagedChatGPTLauncher.exe" "$PSScriptRoot\ControlledActivationOutput.cs"
if($LASTEXITCODE -ne 0){throw 'Controlled helper compilation failed.'}
if($PreviewPath){
 & "$testOutput\WindowLifetimeTests.exe" ([IO.Path]::GetFullPath($LauncherExecutable)) ([IO.Path]::GetFullPath($PreviewPath))
}else{
 & "$testOutput\WindowLifetimeTests.exe" ([IO.Path]::GetFullPath($LauncherExecutable))
}
if($LASTEXITCODE -ne 0){throw 'Window lifetime tests failed.'}
