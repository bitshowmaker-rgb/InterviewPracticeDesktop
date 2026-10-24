param([string]$OutputPath, [long]$MaxPrimaryBytes = 1900000000)
$ErrorActionPreference = 'Stop'
$appRoot = Split-Path -Parent $PSScriptRoot
if (!$OutputPath) { $OutputPath = Join-Path (Split-Path -Parent $appRoot) 'WallpaperEngine-5.5.0-Setup.exe' }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
Add-Type -AssemblyName System.IO.Compression
$allowed = @('Wallpaper Engine.exe','NAudio.Core.dll','NAudio.Wasapi.dll','启动.cmd','README.md','models/ggml-large-v3-turbo.bin','runtime','licenses')
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
                $relative = $file.FullName.Substring($appRoot.Length + 1).Replace([IO.Path]::DirectorySeparatorChar, [char]'/')
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
        if ($MaxPrimaryBytes -le $offset + 16) { throw 'Primary file size limit is too small.' }
        $split = (Get-Item -LiteralPath $zip).Length + $offset + 16 -gt $MaxPrimaryBytes
        $input = [IO.File]::OpenRead($zip)
        try {
            if ($split) {
                $remaining = $MaxPrimaryBytes - $offset - 16
                $buffer = New-Object byte[] 1048576
                while ($remaining -gt 0) {
                    $count = $input.Read($buffer, 0, [int][Math]::Min($buffer.Length, $remaining))
                    if ($count -eq 0) { throw 'Unexpected end of payload.' }
                    $final.Write($buffer, 0, $count); $remaining -= $count
                }
                $continuation = [IO.File]::Create($OutputPath + '.data')
                try { $input.CopyTo($continuation) } finally { $continuation.Dispose() }
            } else {
                $input.CopyTo($final)
                if (Test-Path -LiteralPath ($OutputPath + '.data')) { Remove-Item -LiteralPath ($OutputPath + '.data') }
            }
        } finally { $input.Dispose() }
        $footer = [IO.BinaryWriter]::new($final)
        $signature = if ($split) { 'IPDZIP02' } else { 'IPDZIP01' }
        $footer.Write([Text.Encoding]::ASCII.GetBytes($signature)); $footer.Write([long]$offset); $footer.Flush()
    } finally { $final.Dispose() }
    Get-Item -LiteralPath $OutputPath | Select-Object FullName,Length
    Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256
    if ($split) { Get-Item -LiteralPath ($OutputPath + '.data') | Select-Object FullName,Length; Get-FileHash -LiteralPath ($OutputPath + '.data') -Algorithm SHA256 }
} finally {
    Remove-Item -LiteralPath $stub,$zip -Force -ErrorAction SilentlyContinue
}
