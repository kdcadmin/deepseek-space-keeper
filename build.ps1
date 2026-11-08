param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $root 'bin' } else { $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory) }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$application = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | Select-Object -ExpandProperty FullName)
$referenceResource = '/resource:' + (Join-Path $root 'data\Winapp2-251109.ini') + ',ClearDisk.Winapp2.251109.ini'
$compatibleResource = '/resource:' + (Join-Path $root 'data\bleachbit-5.0.2.sha256') + ',ClearDisk.BleachBit.Hashes'
& $compiler /nologo /utf8output /target:winexe /optimize+ /win32icon:"$root\assets\app.ico" /resource:"$root\assets\app-icon.jpg",ClearDisk.AppIcon /resource:"$root\data\Winapp2.ini",ClearDisk.Winapp2.ini $referenceResource $compatibleResource /platform:x64 /win32manifest:"$root\app.manifest" /r:System.Security.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /out:"$OutputDirectory\CDriveCleaner.exe" $application
if ($LASTEXITCODE) { throw 'Application compilation failed.' }
Copy-Item -LiteralPath (Join-Path $root 'assets\pet') -Destination (Join-Path $OutputDirectory 'pet') -Recurse -Force
Write-Host "Built $OutputDirectory\CDriveCleaner.exe"
