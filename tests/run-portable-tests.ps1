param([Parameter(Mandatory=$true)][string]$WorkRoot)
$ErrorActionPreference = 'Stop'
$appRoot = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path ([IO.Path]::GetFullPath($WorkRoot)) ('portable-test-' + [Guid]::NewGuid().ToString('N'))
foreach ($folder in @('source','assets','models','runtime','licenses','MockInterviewAI-data')) {
    New-Item -ItemType Directory -Path (Join-Path $fixture $folder) -Force | Out-Null
}
Copy-Item -LiteralPath (Join-Path $appRoot 'source\Build-PortableLauncher.ps1'),(Join-Path $appRoot 'source\PortableLauncher.cs') -Destination (Join-Path $fixture 'source')
Copy-Item -LiteralPath (Join-Path $appRoot 'assets\WallpaperEngine.ico') -Destination (Join-Path $fixture 'assets')
foreach ($name in @('Wallpaper Engine.exe','NAudio.Core.dll','NAudio.Wasapi.dll','启动.cmd','README.md','runtime\whisper-server.exe','licenses\notice.txt')) {
    [IO.File]::WriteAllText((Join-Path $fixture $name), 'fixture')
}
[IO.File]::WriteAllText((Join-Path $fixture 'MockInterviewAI-data\settings.json'), 'PRIVATE-SENTINEL')
$bytes = New-Object byte[] 524288
[Random]::new(1234).NextBytes($bytes)
$model = Join-Path $fixture 'models\ggml-large-v3-turbo.bin'
[IO.File]::WriteAllBytes($model, $bytes)
$build = Join-Path $fixture 'source\Build-PortableLauncher.ps1'
function Extract-Package([string]$exe, [string]$destination) {
    $process = Start-Process -FilePath $exe -ArgumentList @('--extract-only', ('"' + $destination + '"')) -WindowStyle Hidden -Wait -PassThru
    return $process.ExitCode
}
foreach ($mode in @('single','split')) {
    $exe = Join-Path $fixture ($mode + '.exe')
    $limit = if ($mode -eq 'split') { 400000 } else { 2000000 }
    & $build -OutputPath $exe -MaxPrimaryBytes $limit | Out-Null
    if ((Test-Path -LiteralPath ($exe + '.data')) -ne ($mode -eq 'split')) { throw 'Unexpected continuation file state.' }
    $destination = Join-Path $fixture ($mode + '-extracted')
    if ((Extract-Package $exe $destination) -ne 0) { throw "$mode extraction failed." }
    if ((Get-FileHash -LiteralPath $model).Hash -ne (Get-FileHash -LiteralPath (Join-Path $destination 'models\ggml-large-v3-turbo.bin')).Hash) { throw 'Extracted model differs.' }
    if (Test-Path -LiteralPath (Join-Path $destination 'MockInterviewAI-data')) { throw 'Private settings were packaged.' }
    New-Item -ItemType Directory -Path (Join-Path $destination 'MockInterviewAI-data') | Out-Null
    $settings = Join-Path $destination 'MockInterviewAI-data\settings.json'
    [IO.File]::WriteAllText($settings, 'PRESERVE-ME')
    Remove-Item -LiteralPath (Join-Path $destination '.package-version')
    if ((Extract-Package $exe $destination) -ne 0 -or [IO.File]::ReadAllText($settings) -ne 'PRESERVE-ME') { throw 'Upgrade did not preserve settings.' }
    Write-Output "$mode extraction, model integrity, clean package and settings preservation: PASS"
}
$missing = Join-Path $fixture 'missing-data.exe'
Copy-Item -LiteralPath (Join-Path $fixture 'split.exe') -Destination $missing
if ((Extract-Package $missing (Join-Path $fixture 'missing-data-extracted')) -eq 0) { throw 'Missing continuation unexpectedly succeeded.' }
Write-Output 'Missing continuation rejected: PASS'
