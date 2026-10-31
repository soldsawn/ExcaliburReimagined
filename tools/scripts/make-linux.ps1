# Builds the Linux package: dist\ExcaliburReimagined-<version>-linux-x64.tar.gz (one self-contained excalibur-reimagined binary,
# install.sh, uninstall.sh, BENI-OKU.txt). Usage:  powershell -ExecutionPolicy Bypass -File tools\scripts\make-linux.ps1
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
[xml]$props = Get-Content (Join-Path $repo 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$name = "ExcaliburReimagined-$version-linux-x64"
$out = Join-Path $repo "dist\$name"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish (Join-Path $repo 'src\Excalibur.Linux') -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $out -v q -nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
foreach ($f in 'install.sh', 'uninstall.sh', 'BENI-OKU.txt') {
    # Linux line endings, whatever git checked out.
    $text = (Get-Content (Join-Path $repo "tools\linux\$f") -Raw -Encoding UTF8) -replace "`r`n", "`n"
    [IO.File]::WriteAllText((Join-Path $out $f), $text, (New-Object Text.UTF8Encoding $false))
}
$tar = Join-Path $repo "dist\$name.tar.gz"
if (Test-Path $tar) { Remove-Item $tar }
tar.exe -czf $tar -C (Join-Path $repo 'dist') $name
if ($LASTEXITCODE -ne 0) { throw 'tar failed' }
Write-Host ("ready: {0} ({1:N0} MB)" -f $tar, ((Get-Item $tar).Length / 1MB))
