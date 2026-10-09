<#
.SYNOPSIS
    Builds signed, sideloadable SysPulse MSIX packages (x64 + ARM64) into dist\.
.DESCRIPTION
    Signs with a code-signing certificate from Cert:\CurrentUser\My whose subject matches the
    Publisher in Package.appxmanifest. Use -CreateCertificate once on a new machine to create a
    self-signed one. The public .cer is copied next to the packages; the private key never leaves
    the certificate store.
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')] [string[]] $Platform = @('x64', 'ARM64'),
    [string] $Thumbprint,
    [switch] $CreateCertificate
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'SysPulse\SysPulse\SysPulse.csproj'
$manifestPath = Join-Path $root 'SysPulse\SysPulse\Package.appxmanifest'
$dist = Join-Path $root 'dist'

[xml] $manifest = Get-Content $manifestPath
$publisher = $manifest.Package.Identity.Publisher
$version = $manifest.Package.Identity.Version
$codeSigningOid = '1.3.6.1.5.5.7.3.3'

if (-not $Thumbprint) {
    $cert = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $publisher -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) -and $_.EnhancedKeyUsageList.ObjectId -contains $codeSigningOid } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
    if (-not $cert -and $CreateCertificate) {
        $cert = New-SelfSignedCertificate -Type Custom -Subject $publisher -KeyUsage DigitalSignature `
            -FriendlyName 'SysPulse code signing' -CertStoreLocation Cert:\CurrentUser\My `
            -TextExtension @("2.5.29.37={text}$codeSigningOid", '2.5.29.19={text}') -NotAfter (Get-Date).AddYears(5)
    }
    if (-not $cert) { throw "No code-signing certificate for '$publisher' in Cert:\CurrentUser\My. Re-run with -CreateCertificate." }
    $Thumbprint = $cert.Thumbprint
}

# A running extension locks files in the build output.
Get-Process -Name 'SysPulse' -ErrorAction SilentlyContinue | Stop-Process -Force

New-Item -ItemType Directory -Force $dist | Out-Null
$work = Join-Path $root 'SysPulse\SysPulse\AppPackages'

foreach ($p in $Platform) {
    $rid = if ($p -eq 'x64') { 'win-x64' } else { 'win-arm64' }
    dotnet publish $project -c Release -p:Platform=$p -p:RuntimeIdentifier=$rid `
        -p:GenerateAppxPackageOnBuild=true -p:AppxBundle=Never -p:UapAppxPackageBuildMode=SideloadOnly `
        -p:AppxPackageSigningEnabled=true -p:PackageCertificateThumbprint=$Thumbprint `
        -p:AppxPackageDir="$work\" -v:minimal
    if ($LASTEXITCODE -ne 0) { throw "Packaging failed for $p ($LASTEXITCODE)" }

    $msix = Get-ChildItem $work -Recurse -Filter "SysPulse_${version}_$p.msix" |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $msix) { throw "SysPulse_${version}_$p.msix not found under $work" }
    Copy-Item $msix.FullName $dist -Force
}

Export-Certificate -Cert "Cert:\CurrentUser\My\$Thumbprint" -FilePath (Join-Path $dist 'SysPulse.cer') | Out-Null
Get-ChildItem $dist | Format-Table Name, Length
