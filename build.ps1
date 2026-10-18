# Builds HotkeyMute.exe with the C# compiler that ships with Windows (no SDK needed).
Set-Location $PSScriptRoot
Add-Type -AssemblyName System.Drawing

# Icon: orange circle with a white speaker + X
$bmp = New-Object System.Drawing.Bitmap 48, 48
$g = [System.Drawing.Graphics]::FromImage($bmp); $g.SmoothingMode = 'AntiAlias'
$g.FillEllipse([System.Drawing.Brushes]::OrangeRed, 0, 0, 47, 47)
$P = { param($x, $y) [System.Drawing.Point]::new([int]($x * 0.75), [int]($y * 0.75)) }
$g.FillPolygon([System.Drawing.Brushes]::White, @((& $P 14 25), (& $P 24 25), (& $P 36 15), (& $P 36 49), (& $P 24 39), (& $P 14 39)))
$pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 4
$g.DrawLine($pen, 32, 18, 42, 30); $g.DrawLine($pen, 42, 18, 32, 30)
$icon = [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
$fs = [System.IO.File]::Create("$PSScriptRoot\HotkeyMute.ico"); $icon.Save($fs); $fs.Close()

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /target:winexe /optimize+ /out:HotkeyMute.exe /win32icon:HotkeyMute.ico `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll HotkeyMute.cs
if ($LASTEXITCODE -eq 0) { Write-Host "Built $PSScriptRoot\HotkeyMute.exe" } else { exit 1 }
