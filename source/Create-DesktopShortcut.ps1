param([string]$ShortcutPath = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Wallpaper Engine.lnk'))
$ErrorActionPreference = 'Stop'
$appRoot = Split-Path -Parent $PSScriptRoot
$executable = Join-Path $appRoot 'Wallpaper Engine.exe'
$model = Join-Path $appRoot 'models\ggml-large-v3-turbo-q5_0.bin'
foreach ($required in @($executable, $model)) {
    if (!(Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required application file missing: $required" }
}
$cpuRuntime = Join-Path $appRoot 'runtime\whisper\whisper-server.exe'
$gpuRuntime = Join-Path $appRoot 'runtime\whisper-gpu\whisper-server.exe'
if (!(Test-Path -LiteralPath $cpuRuntime -PathType Leaf) -and !(Test-Path -LiteralPath $gpuRuntime -PathType Leaf)) {
    throw 'Speech recognition runtime is missing from the application folder.'
}
$ShortcutPath = [IO.Path]::GetFullPath($ShortcutPath)
if ([IO.Path]::GetExtension($ShortcutPath) -ne '.lnk') { throw 'The shortcut path must end in .lnk.' }
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($ShortcutPath)
$shortcut.TargetPath = $executable
$shortcut.WorkingDirectory = $appRoot
$shortcut.IconLocation = $executable + ',0'
$shortcut.Description = 'Wallpaper Engine'
$shortcut.Save()
Write-Output "Shortcut: $ShortcutPath"
