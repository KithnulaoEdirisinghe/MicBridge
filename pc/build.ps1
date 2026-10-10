# Builds dist\MicBridge.exe with the C# compiler that ships inside Windows (.NET Framework 4.x).
# Nothing needs to be installed. Run from anywhere:   powershell -ExecutionPolicy Bypass -File pc\build.ps1

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$root = Split-Path -Parent $here
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'The .NET Framework C# compiler (csc.exe) was not found.' }

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$out = Join-Path $dist 'MicBridge.exe'

# If MicBridge is running, Windows will not let its file be overwritten, but it can be moved aside.
# The running copy carries on untouched; the new build is used the next time MicBridge is started.
$old = Join-Path $dist 'MicBridge.old.exe'
if (Test-Path -LiteralPath $old) { try { [IO.File]::Delete($old) } catch { } }
if (Test-Path -LiteralPath $out) {
    $inUse = $false
    try { $stream = [IO.File]::Open($out, 'Open', 'ReadWrite', 'None'); $stream.Dispose() } catch { $inUse = $true }
    if ($inUse) { [IO.File]::Move($out, $old); 'MicBridge is running: it keeps using the old build until you restart it.' }
}

# 32-bit preferred: the app needs very little memory and a 32-bit process uses noticeably less of it.
& $csc /nologo /target:winexe /optimize+ /debug- /unsafe /warn:4 /warnaserror+ `
    /platform:anycpu32bitpreferred /filealign:512 `
    "/out:$out" "/win32icon:$here\app.ico" "/win32manifest:$here\app.manifest" `
    /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
    "$here\src\*.cs"
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
"Built $out  ($([math]::Round((Get-Item $out).Length / 1KB)) KB)"
