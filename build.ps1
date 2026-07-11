# Builds LingCodeFTP.exe with the .NET Framework C# compiler (no SDK needed).
$ErrorActionPreference = "Stop"
$csc  = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$root = $PSScriptRoot
$out  = Join-Path $root "LingCodeFTP.exe"
$src  = Get-ChildItem (Join-Path $root "src") -Filter *.cs | ForEach-Object { $_.FullName }

& $csc /nologo /target:winexe /out:$out `
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll `
  /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll `
  $src

if ($LASTEXITCODE -ne 0) { Write-Host "`nBUILD FAILED"; exit 1 }
Write-Host "`nBuilt $out"
