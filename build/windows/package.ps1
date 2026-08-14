<#
.SYNOPSIS
    Builds Windows artifacts for one runtime identifier.

.DESCRIPTION
    Produces:
      dist\Omnigit-<version>-<rid>.zip              portable, unzip and run
      dist\Omnigit-<version>-<rid>-setup.exe        Inno Setup installer

.PARAMETER Rid
    win-x64 or win-arm64.

.PARAMETER Version
    Defaults to <Version> in Omnigit.csproj, which is the one place a version is
    written down - see build/version.sh for why.

.EXAMPLE
    pwsh build/windows/package.ps1 -Rid win-x64
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Rid = 'win-x64',

    [string]$Version
)

$ErrorActionPreference = 'Stop'

$root  = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$dist  = Join-Path $root 'dist'
$stage = Join-Path $root "build\.stage-$Rid"

# build/version.sh is the bash half of this; there is no sourcing a shell script
# from PowerShell, so the read is repeated rather than shared.
if (-not $Version) {
    $csproj = Join-Path $root 'src\Omnigit\Omnigit.csproj'
    $Version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version |
        Where-Object { $_ } | Select-Object -First 1

    if (-not $Version) { throw "no <Version> in $csproj" }
}

Write-Host "==> Publishing $Rid (self-contained, $Version)"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $dist, $stage | Out-Null

dotnet publish (Join-Path $root 'src\Omnigit\Omnigit.csproj') `
    --configuration Release `
    --runtime $Rid `
    --self-contained true `
    -p:Version=$Version `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    --output $stage
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "==> Portable zip"
$zip = Join-Path $dist "Omnigit-$Version-$Rid.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip

Write-Host "==> Installer"
$iscc = Get-Command 'iscc.exe' -ErrorAction SilentlyContinue
if (-not $iscc) {
    $candidate = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
    if (Test-Path $candidate) {
        $iscc = $candidate
    } else {
        Write-Warning "Inno Setup not found - skipping installer. Install with: choco install innosetup -y"
        Get-ChildItem $dist
        exit 0
    }
} else {
    $iscc = $iscc.Source
}

& $iscc `
    "/DAppVersion=$Version" `
    "/DSourceDir=$stage" `
    "/DOutputDir=$dist" `
    "/DRid=$Rid" `
    (Join-Path $PSScriptRoot 'omnigit.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }

Write-Host "==> Done:"
Get-ChildItem $dist
