param([string]$OutputName = 'Wallpaper Engine.exe')
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$wpf = Join-Path $framework 'WPF'
$appRoot = Split-Path -Parent $PSScriptRoot
$references = @('System.dll','System.Core.dll','System.Net.Http.dll','System.Web.Extensions.dll','System.Security.dll','System.Drawing.dll','System.Windows.Forms.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$references += @('PresentationFramework.dll','PresentationCore.dll','WindowsBase.dll','System.Xaml.dll') | ForEach-Object {
    $assembly = Join-Path $wpf $_
    if (!(Test-Path -LiteralPath $assembly)) { $assembly = Join-Path $framework $_ }
    '/reference:' + $assembly
}
$references += '/reference:C:\Windows\Microsoft.NET\assembly\GAC_MSIL\netstandard\v4.0_2.0.0.0__cc7b13ffcd2ddd51\netstandard.dll'
foreach ($dll in @('NAudio.Core.dll','NAudio.Wasapi.dll')) {
    Copy-Item -LiteralPath (Join-Path $appRoot ('lib\' + $dll)) -Destination (Join-Path $appRoot $dll) -Force
    $references += '/reference:' + (Join-Path $appRoot $dll)
}
$compilerArgs = @('/nologo','/target:winexe','/platform:x64','/optimize+','/utf8output','/codepage:65001',('/out:' + (Join-Path $appRoot $OutputName)),('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')),('/resource:' + (Join-Path $PSScriptRoot 'MainWindow.xaml') + ',MainWindow.xaml'))
$compilerArgs += $references
$compilerArgs += '/win32icon:' + (Join-Path $appRoot 'assets\WallpaperEngine.ico')
$compilerArgs += '/resource:' + (Join-Path $appRoot 'assets\WallpaperEngine.ico') + ',WallpaperEngine.ico'
$compilerArgs += '/resource:' + (Join-Path $appRoot 'answer-templates\interview-answer-templates.md') + ',AnswerSkill.md'
$compilerArgs += '/resource:' + (Join-Path $appRoot 'answer-templates\templates.md') + ',AnswerTemplates.md'
$compilerArgs += @('AssemblyInfo.cs','AnswerTemplatePolicy.cs','InterviewCore.cs','AnswerStreaming.cs','AutomaticQuestionBuffer.cs','WindowAppearance.cs','WindowMovement.cs','CaptureProtection.cs','LoopbackAudio.cs','WhisperEngine.cs','SpeakerTranscription.cs','DesktopApp.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& (Join-Path $framework 'csc.exe') $compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Desktop compilation failed' }
Write-Output "Built: $(Join-Path $appRoot $OutputName)"
