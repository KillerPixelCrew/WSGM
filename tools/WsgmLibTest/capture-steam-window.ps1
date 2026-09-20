param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class WsgmWindowCapture
{
    public delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    public static extern int GetWindowText(IntPtr handle, StringBuilder text, int maximum);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr handle, out Rect rect);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr handle, int command);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr handle, IntPtr deviceContext, uint flags);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
}
"@

$steamWindow = [IntPtr]::Zero
$callback = [WsgmWindowCapture+EnumWindowsProc] {
    param([IntPtr]$handle, [IntPtr]$parameter)
    if (-not [WsgmWindowCapture]::IsWindowVisible($handle)) {
        return $true
    }

    $title = [Text.StringBuilder]::new(512)
    [void][WsgmWindowCapture]::GetWindowText($handle, $title, $title.Capacity)
    if ($title.ToString() -eq "Big-Picture-Modus" -or $title.ToString() -eq "Steam Big Picture Mode") {
        $script:steamWindow = $handle
        return $false
    }

    return $true
}

[void][WsgmWindowCapture]::EnumWindows($callback, [IntPtr]::Zero)
if ($steamWindow -eq [IntPtr]::Zero) {
    throw "Steam Big Picture window was not found."
}

$processId = [uint32]0
[void][WsgmWindowCapture]::GetWindowThreadProcessId($steamWindow, [ref]$processId)
$process = Get-Process -Id $processId -ErrorAction Stop
if ($process.ProcessName -ne "steamwebhelper") {
    throw "The Big Picture window belongs to $($process.ProcessName), not steamwebhelper."
}

[void][WsgmWindowCapture]::ShowWindow($steamWindow, 9)
[void](New-Object -ComObject WScript.Shell).SendKeys("%")
[void][WsgmWindowCapture]::SetForegroundWindow($steamWindow)
Start-Sleep -Milliseconds 750
if ([WsgmWindowCapture]::GetForegroundWindow() -ne $steamWindow) {
    throw "Steam Big Picture could not be brought to the foreground."
}

$rect = [WsgmWindowCapture+Rect]::new()
if (-not [WsgmWindowCapture]::GetWindowRect($steamWindow, [ref]$rect)) {
    throw "Steam Big Picture window bounds were unavailable."
}

$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top
if ($width -lt 1 -or $height -lt 1) {
    throw "Steam Big Picture window has invalid bounds."
}

$resolved = [IO.Path]::GetFullPath($OutputPath)
$bitmap = [Drawing.Bitmap]::new($width, $height)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
try {
    $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
    $bitmap.Save($resolved, [Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $graphics.Dispose()
    $bitmap.Dispose()
}

Write-Output "wrote $resolved ($($width)x$($height)) from steamwebhelper $processId"
