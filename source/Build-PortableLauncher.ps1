param([string]$OutputPath)
$ErrorActionPreference = 'Stop'
$appRoot = Split-Path -Parent $PSScriptRoot
if (!$OutputPath) { $OutputPath = Join-Path (Split-Path -Parent $appRoot) 'WallpaperEngine-5.4.0-Setup.exe' }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
Add-Type -AssemblyName System.IO.Compression
$allowed = @('Wallpaper Engine.exe','NAudio.Core.dll','NAudio.Wasapi.dll','启动.cmd','README.md','依赖来源.json','models','runtime','licenses')
foreach ($name in $allowed) { if (!(Test-Path -LiteralPath (Join-Path $appRoot $name))) { throw "Missing package input: $name" } }
$stub = $OutputPath + '.stub.exe'
$zip = $OutputPath + '.payload.zip'
$argsList = @('/nologo','/target:winexe','/platform:x64','/optimize+','/codepage:65001',('/out:' + $stub),('/win32icon:' + (Join-Path $appRoot 'assets\WallpaperEngine.ico')))
$argsList += @('System.dll','System.Core.dll','System.Windows.Forms.dll','System.Drawing.dll','System.IO.Compression.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$argsList += Join-Path $PSScriptRoot 'PortableLauncher.cs'
& (Join-Path $framework 'csc.exe') $argsList
if ($LASTEXITCODE -ne 0) { throw 'Launcher compilation failed' }
try {
    $stream = [IO.File]::Create($zip)
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $allowed) {
            $item = Get-Item -LiteralPath (Join-Path $appRoot $name)
            $files = if ($item.PSIsContainer) { Get-ChildItem -LiteralPath $item.FullName -File -Recurse } else { @($item) }
            foreach ($file in $files) {
                $relative = $file.FullName.Substring($appRoot.Length + 1).Replace('\','/')
                $entry = $archive.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
                $output = $entry.Open(); $input = [IO.File]::OpenRead($file.FullName)
                try { $input.CopyTo($output) } finally { $input.Dispose(); $output.Dispose() }
            }
        }
    } finally { $archive.Dispose(); $stream.Dispose() }
    $final = [IO.File]::Create($OutputPath)
    try {
        $input = [IO.File]::OpenRead($stub)
        try { $input.CopyTo($final) } finally { $input.Dispose() }
        $offset = $final.Position
        $input = [IO.File]::OpenRead($zip)
        try { $input.CopyTo($final) } finally { $input.Dispose() }
        $footer = [IO.BinaryWriter]::new($final)
        $footer.Write([Text.Encoding]::ASCII.GetBytes('IPDZIP01')); $footer.Write([long]$offset); $footer.Flush()
    } finally { $final.Dispose() }
    Get-Item -LiteralPath $OutputPath | Select-Object FullName,Length
    Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256
} finally {
    Remove-Item -LiteralPath $stub,$zip -Force -ErrorAction SilentlyContinue
}
