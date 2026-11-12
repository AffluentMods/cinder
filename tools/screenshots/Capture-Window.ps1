<#
.SYNOPSIS
    Saves one window's own content to a PNG.

.DESCRIPTION
    Uses PrintWindow, not a screen grab: only the target window is captured, at the scale of the
    monitor it is on, and nothing else on the desktop can end up in the image even when the
    window is partly covered. Used for the documentation screenshots (docs/screenshots.md)
    together with Cinder's --shot ... --hold switch, which keeps the window open long enough.

.EXAMPLE
    $p = Start-Process .\Cinder.exe -ArgumentList '--shot','render.png','--tool','hex','--hold','12' -PassThru
    Start-Sleep -Seconds 8
    .\Capture-Window.ps1 -ProcessId $p.Id -Out hex.png
#>
param(
    [Parameter(Mandatory)] [int]$ProcessId,
    [Parameter(Mandatory)] [string]$Out
)

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class CinderCapture {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern int GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
}
'@

# Per-monitor DPI awareness, so the rectangle is in real pixels rather than scaled ones.
[void][CinderCapture]::SetProcessDpiAwarenessContext([IntPtr](-4))

$window = (Get-Process -Id $ProcessId).MainWindowHandle
if ($window -eq [IntPtr]::Zero) { throw "Process $ProcessId has no main window yet." }

$rect = New-Object CinderCapture+RECT
[void][CinderCapture]::GetWindowRect($window, [ref]$rect)
$width = $rect.R - $rect.L
$height = $rect.B - $rect.T

$bitmap = New-Object System.Drawing.Bitmap $width, $height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$dc = $graphics.GetHdc()
$ok = [CinderCapture]::PrintWindow($window, $dc, 2)   # PW_RENDERFULLCONTENT
$graphics.ReleaseHdc($dc)
$graphics.Dispose()
if (-not $ok) { $bitmap.Dispose(); throw "PrintWindow failed for process $ProcessId." }

$bitmap.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()
"Captured ${width}x${height} at $([CinderCapture]::GetDpiForWindow($window)) dpi -> $Out"
