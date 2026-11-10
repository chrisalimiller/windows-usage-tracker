<#
  Builds the Microsoft Store package: one .msix per architecture, bundled into a .msixbundle
  that can be uploaded to Partner Center. The Store signs it, so it is left unsigned here.

  Expects the Store build of the exe at out/store-<arch>/WindowsUsageTracker.exe, made with:
    dotnet publish src/UsageTracker -c Release -r win-<arch> --self-contained
      -p:PublishSingleFile=true -p:StoreBuild=true -o out/store-<arch>
#>
param(
  [string[]] $Architectures = @('x64', 'arm64'),
  [string] $OutDir = 'out/store'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$identity = Get-Content packaging/store-identity.json -Raw | ConvertFrom-Json
[xml] $props = Get-Content Directory.Build.props
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw 'No <Version> in Directory.Build.props' }
$packageVersion = "$version.0"

$sdkBin = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Directory |
  Where-Object { $_.Name -match '^10\.' -and (Test-Path (Join-Path $_.FullName 'x64\makeappx.exe')) } |
  Sort-Object { [version] $_.Name } -Descending | Select-Object -First 1
if (-not $sdkBin) { throw 'makeappx.exe not found; install the Windows 10/11 SDK' }
$makeappx = Join-Path $sdkBin.FullName 'x64\makeappx.exe'
Write-Host "Using $makeappx, package version $packageVersion"

New-Item -ItemType Directory -Force $OutDir | Out-Null
$bundleDir = Join-Path $OutDir 'bundle'
Remove-Item -Recurse -Force $bundleDir -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $bundleDir | Out-Null

foreach ($arch in $Architectures) {
  $exe = "out/store-$arch/WindowsUsageTracker.exe"
  if (-not (Test-Path $exe)) { throw "Missing $exe" }

  $layout = Join-Path $OutDir "layout-$arch"
  Remove-Item -Recurse -Force $layout -ErrorAction SilentlyContinue
  New-Item -ItemType Directory -Force (Join-Path $layout 'Assets') | Out-Null
  Copy-Item $exe $layout
  Copy-Item packaging/Assets/*.png (Join-Path $layout 'Assets')

  $manifest = (Get-Content packaging/Package.appxmanifest -Raw).
    Replace('$PackageName$', $identity.PackageName).
    Replace('$Publisher$', [Security.SecurityElement]::Escape($identity.Publisher)).
    Replace('$PublisherDisplayName$', [Security.SecurityElement]::Escape($identity.PublisherDisplayName)).
    Replace('$Version$', $packageVersion).
    Replace('$Arch$', $arch)
  Set-Content -Path (Join-Path $layout 'AppxManifest.xml') -Value $manifest -Encoding UTF8

  $msix = Join-Path $bundleDir "WindowsUsageTracker_${packageVersion}_$arch.msix"
  & $makeappx pack /o /d $layout /p $msix
  if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed for $arch" }
}

$bundle = Join-Path $OutDir "WindowsUsageTracker_${packageVersion}.msixbundle"
& $makeappx bundle /o /bv $packageVersion /d $bundleDir /p $bundle
if ($LASTEXITCODE -ne 0) { throw 'makeappx bundle failed' }
Write-Host "Built $bundle"
