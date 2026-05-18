param(
    [string]$OutDir = "temp/screenshots",
    [string]$ProcessName = "apr-pc"
)

# Capture the apr-pc main window to a PNG using PrintWindow (works even
# when the window is hidden behind another). Falls back to CopyFromScreen
# if PrintWindow returns empty.

Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdcBlt, uint nFlags);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
}
"@

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) {
    Write-Output "NO_PROCESS: $ProcessName"
    exit 1
}

$h = $proc.MainWindowHandle
$rect = New-Object W+RECT
[void][W]::GetWindowRect($h, [ref]$rect)
$w  = $rect.R - $rect.L
$ht = $rect.B - $rect.T
if ($w -le 0 -or $ht -le 0) {
    Write-Output "BAD_RECT: $w x $ht"
    exit 2
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$ts   = Get-Date -Format "yyyyMMdd-HHmmss"
$file = Join-Path $OutDir "aprpc-$ts.png"

$bmp = New-Object System.Drawing.Bitmap $w, $ht
$g   = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
# PW_RENDERFULLCONTENT = 0x00000002 — captures DWM-composited content
$ok  = [W]::PrintWindow($h, $hdc, 0x00000002)
$g.ReleaseHdc($hdc)

$bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

Write-Output "OK: $file ($w x $ht, printwindow_ok=$ok)"
