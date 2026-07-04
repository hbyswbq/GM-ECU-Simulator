# Publish-Release.ps1 - assemble a framework-dependent release bundle for a
# GitHub release. Produces the FLAT layout Register.ps1 expects: the published
# EXE, both native shim DLLs, and the ShimInstaller\ scripts all at the bundle root.
# This is the missing automation behind the v0.2.0 shim issue - dotnet publish
# never includes the native shims (they are a separate C++ vcxproj), so a
# hand-zipped publish folder ships without them and Register.ps1 then fails its
# "Required artifact missing" check.
#
# Usage:
#   .\ShimInstaller\Publish-Release.ps1 -Version v0.3.0
#   .\ShimInstaller\Publish-Release.ps1 -Version v0.3.0 -SkipBuild   # reuse existing Release binaries
#
# Output: <repo>\dist\GmEcuSimulator-<Version>-win-x64.zip  (dist\ is gitignored).
# No elevation needed - this only builds and zips. Registration is Register.ps1.
#
# Framework-dependent: the target machine needs the .NET 9 Desktop Runtime.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Version,
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'

# ShimInstaller\ is a subdir of the repo root, so the parent of this script is root.
$repoRoot = Split-Path -Parent $PSScriptRoot
$shimProj = Join-Path $repoRoot "PassThruShim\PassThruShim.vcxproj"
$appProj  = Join-Path $repoRoot "GmEcuSimulator\GmEcuSimulator.csproj"
$publish  = Join-Path $repoRoot "GmEcuSimulator\bin\Release\net9.0-windows\publish"
$shim64   = Join-Path $repoRoot "PassThruShim\x64\Release\PassThruShim64.dll"
$shim32   = Join-Path $repoRoot "PassThruShim\Release\PassThruShim32.dll"

# --- Build (Release) -------------------------------------------------------

if (-not $SkipBuild) {
    $msbuild = "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
    if (-not (Test-Path $msbuild)) { throw "MSBuild not found at $msbuild" }

    Write-Host "Building 64-bit shim (Release)..."
    & $msbuild $shimProj /p:Configuration=Release /p:Platform=x64 /nologo /v:minimal | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "MSBuild x64 failed (exit $LASTEXITCODE)" }

    Write-Host "Building 32-bit shim (Release)..."
    & $msbuild $shimProj /p:Configuration=Release /p:Platform=Win32 /nologo /v:minimal | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "MSBuild Win32 failed (exit $LASTEXITCODE)" }

    # Clean the publish dir so stale files from a prior layout never leak in.
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

    # Stamp the published bundle with exactly this release's version so the
    # in-app About box reports the GitHub release it shipped as. The csproj
    # otherwise derives the version from `git describe`; passing it explicitly
    # is authoritative even when the publish runs before the tag is pushed.
    # Strip the leading 'v' for the numeric assembly Version.
    $verNum = $Version.TrimStart('v')

    Write-Host "Publishing WPF app (Release, framework-dependent) as $verNum..."
    & dotnet publish $appProj -c Release "/p:Version=$verNum" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }
}

# --- Validate build inputs exist -------------------------------------------

foreach ($p in @($shim64, $shim32, (Join-Path $publish "GmEcuSimulator.exe"))) {
    if (-not (Test-Path $p)) { throw "Build artifact missing: $p (run without -SkipBuild)" }
}

# --- Stage the flat bundle -------------------------------------------------

$stage = Join-Path $env:TEMP ("GmEcuSim-pkg-" + $Version)
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

# Published app (exe + deps + the ShimInstaller\ subdir the csproj copies in) lands
# at the bundle root.
Copy-Item (Join-Path $publish '*') -Destination $stage -Recurse -Force
# Both native shims sit flat beside the exe - this is exactly what Register.ps1
# probes for via Split-Path -Parent $PSScriptRoot.
Copy-Item $shim64 -Destination $stage -Force
Copy-Item $shim32 -Destination $stage -Force

# --- Verify the flat layout Register.ps1 needs -----------------------------

$required = 'GmEcuSimulator.exe', 'PassThruShim64.dll', 'PassThruShim32.dll', 'ShimInstaller\Register.ps1'
foreach ($r in $required) {
    if (-not (Test-Path (Join-Path $stage $r))) { throw "Bundle missing required artifact: $r" }
}

# --- Zip -------------------------------------------------------------------

$outDir = Join-Path $repoRoot "dist"
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$zip = Join-Path $outDir ("GmEcuSimulator-" + $Version + "-win-x64.zip")
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
Remove-Item $stage -Recurse -Force

$zi = Get-Item $zip
Write-Host ""
Write-Host ("Bundle ready: {0} ({1:N1} MB)" -f $zi.FullName, ($zi.Length / 1MB))
Write-Host ""
Write-Host "Next - create the GitHub release (target machine needs the .NET 9 Desktop Runtime):"
Write-Host ("  gh release create $Version --prerelease --title ""$Version (pre-release)"" --notes-file <notes.md> ""$($zi.FullName)""")
