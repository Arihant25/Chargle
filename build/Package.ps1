<#
.SYNOPSIS
    Builds Chargle for distribution.

.DESCRIPTION
    Two shapes come out of this repository:

      portable   A self contained folder. Nothing to install first, which is what you want for
                 a GitHub release, and the reason it is a few hundred megabytes.

      msix       The packaged build, for the Store or for sideloading. Needs a signing
                 certificate whose subject matches the Publisher in Package.appxmanifest.
                 The Store re-signs with its own identity on submission, so an unsigned
                 package is enough to upload.

.EXAMPLE
    ./build/Package.ps1 -Kind portable -Rid win-x64
    ./build/Package.ps1 -Kind msix -Rid win-x64
#>

[CmdletBinding()]
param(
    [ValidateSet('portable', 'msix', 'store')]
    [string]$Kind = 'portable',

    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Rid = 'win-x64',

    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/Chargle/Chargle.csproj'
$platform = if ($Rid -eq 'win-arm64') { 'ARM64' } else { 'x64' }
$output = Join-Path $root "build/$Kind-$Rid"

Write-Host "Chargle: $Kind for $Rid" -ForegroundColor Cyan

# The generators run first so a release can never ship assets that lag behind their source.
Write-Host 'Regenerating sounds and icons'
dotnet run --project (Join-Path $root 'tools/SoundForge') -- (Join-Path $root 'src/Chargle/Assets/Sounds')
dotnet run --project (Join-Path $root 'tools/IconForge') -- (Join-Path $root 'src/Chargle/Assets')

if ($Kind -eq 'portable') {
    dotnet publish $project `
        -c $Configuration `
        -r $Rid `
        -p:Platform=$platform `
        -p:WindowsPackageType=None `
        -o $output

    $zip = Join-Path $root "build/Chargle-$Rid.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path "$output/*" -DestinationPath $zip

    $size = (Get-Item $zip).Length / 1MB
    Write-Host ("Wrote {0} ({1:F0} MB)" -f $zip, $size) -ForegroundColor Green
}
elseif ($Kind -eq 'msix') {
    dotnet publish $project `
        -c $Configuration `
        -r $Rid `
        -p:Platform=$platform `
        -p:WindowsPackageType=MSIX `
        -p:GenerateAppxPackageOnBuild=true `
        -p:AppxSymbolPackageEnabled=false `
        -p:AppxPackageSigningEnabled=false `
        -o $output

    Write-Host "Wrote $output" -ForegroundColor Green
    Write-Host 'Sign before sideloading, for example:'
    Write-Host '  signtool sign /fd SHA256 /a /f your.pfx /p <password> <package>.msix'
}
else {
    # Two flags here are not optional and both cost an afternoon to work out.
    #
    # AppxSymbolPackageEnabled=false, because Store upload mode otherwise tries to convert
    # FastLink PDBs using mspdbcmf.exe, which only ships with Visual Studio, and fails the whole
    # build with a misleading "Invalid parameters" error when it is missing.
    #
    # One architecture per invocation, because bundling x64 and arm64 together needs a
    # RID-neutral build and this project is RID-specific. Partner Center happily takes both
    # .msixupload files in the same submission, so a bundle is not needed.
    dotnet publish $project `
        -c $Configuration `
        -r $Rid `
        -p:Platform=$platform `
        -p:WindowsPackageType=MSIX `
        -p:GenerateAppxPackageOnBuild=true `
        -p:UapAppxPackageBuildMode=StoreUpload `
        -p:AppxSymbolPackageEnabled=false `
        -p:AppxPackageSigningEnabled=false

    $upload = Get-ChildItem (Join-Path (Split-Path $project) 'AppPackages') -Filter *.msixupload -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1

    if ($upload) {
        Write-Host ("Upload this to Partner Center: {0} ({1:F0} MB)" -f $upload.FullName, ($upload.Length / 1MB)) -ForegroundColor Green
    }

    Write-Host 'Leave it unsigned. The Store signs submissions with its own certificate.'
    Write-Host 'Identity Name and Publisher in Package.appxmanifest must match Partner Center first.'
}
