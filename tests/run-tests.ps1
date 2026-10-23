$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$appRoot = Split-Path -Parent $PSScriptRoot
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('MockInterviewAI-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
$arguments = @('/nologo','/target:exe','/platform:x64','/codepage:65001',('/out:' + (Join-Path $temporaryRoot 'CoreTests.exe')))
$arguments += @('System.dll','System.Core.dll','System.Net.Http.dll','System.Web.Extensions.dll','System.Security.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$arguments += '/resource:' + (Join-Path $appRoot 'answer-templates\interview-answer-templates.md') + ',AnswerSkill.md'
$arguments += '/resource:' + (Join-Path $appRoot 'answer-templates\templates.md') + ',AnswerTemplates.md'
$arguments += @((Join-Path $appRoot 'source\InterviewCore.cs'), (Join-Path $appRoot 'source\AnswerStreaming.cs'), (Join-Path $appRoot 'source\AnswerTemplatePolicy.cs'), (Join-Path $appRoot 'source\AutomaticQuestionBuffer.cs'), (Join-Path $PSScriptRoot 'CoreTests.cs'))
& (Join-Path $framework 'csc.exe') $arguments
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed' }
& (Join-Path $temporaryRoot 'CoreTests.exe') $temporaryRoot
if ($LASTEXITCODE -ne 0) { throw 'Core tests failed' }
$streamingArguments = @(('/out:' + (Join-Path $temporaryRoot 'StreamingTests.exe')))
$streamingArguments += @($arguments | Where-Object { !($_.StartsWith('/out:')) -and $_ -ne (Join-Path $PSScriptRoot 'CoreTests.cs') })
$streamingArguments += Join-Path $PSScriptRoot 'StreamingTests.cs'
& (Join-Path $framework 'csc.exe') $streamingArguments
if ($LASTEXITCODE -ne 0) { throw 'Streaming test compilation failed' }
& (Join-Path $temporaryRoot 'StreamingTests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Streaming tests failed' }
$audioReferences = @('System.dll','System.Core.dll','System.Net.Http.dll','System.Web.Extensions.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$audioReferences += '/reference:C:\Windows\Microsoft.NET\assembly\GAC_MSIL\netstandard\v4.0_2.0.0.0__cc7b13ffcd2ddd51\netstandard.dll'
foreach ($dll in @('NAudio.Core.dll','NAudio.Wasapi.dll')) {
    Copy-Item -LiteralPath (Join-Path $appRoot $dll) -Destination (Join-Path $temporaryRoot $dll)
    $audioReferences += '/reference:' + (Join-Path $appRoot $dll)
}
foreach ($suite in @('LoopbackTests','TranscriptionTests','WhisperEngineTests')) {
    $testArguments = @('/nologo','/target:exe','/platform:x64','/codepage:65001',('/out:' + (Join-Path $temporaryRoot ($suite + '.exe'))))
    $testArguments += $audioReferences
    $testArguments += @('LoopbackAudio.cs','SpeakerTranscription.cs','WhisperEngine.cs') | ForEach-Object { Join-Path $appRoot ('source\' + $_) }
    $testArguments += Join-Path $PSScriptRoot ($suite + '.cs')
    & (Join-Path $framework 'csc.exe') $testArguments
    if ($LASTEXITCODE -ne 0) { throw "$suite compilation failed" }
    & (Join-Path $temporaryRoot ($suite + '.exe'))
    if ($LASTEXITCODE -ne 0) { throw "$suite failed" }
}
Write-Output "Test artifacts: $temporaryRoot"
