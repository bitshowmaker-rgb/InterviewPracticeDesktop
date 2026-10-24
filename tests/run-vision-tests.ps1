param([string]$AppExecutable = 'Wallpaper Engine.exe')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$testExe = Join-Path $root 'VisionRequestTests.exe'
$arguments = @('/nologo', '/target:exe', '/platform:x64', '/utf8output', ('/out:' + $testExe), ('/reference:' + (Join-Path $root $AppExecutable)))
$arguments += @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Net.Http.dll', 'System.Web.Extensions.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$arguments += Join-Path $PSScriptRoot 'VisionRequestTests.cs'
try {
    & (Join-Path $framework 'csc.exe') $arguments
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
    & $testExe
    if ($LASTEXITCODE -ne 0) { throw 'Vision request regression failed.' }
} finally {
    if (Test-Path -LiteralPath $testExe) { Remove-Item -LiteralPath $testExe }
}
