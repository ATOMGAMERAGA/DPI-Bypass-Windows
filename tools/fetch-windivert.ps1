<#
.SYNOPSIS
    Downloads the WinDivert driver payload that ships with Atom DPI Bypass.

.DESCRIPTION
    The 64-bit WinDivert.dll and WinDivert64.sys are placed in third-party/windivert
    so the app project copies them next to the executable.

    Two independent checks are applied:

      * The archive is pinned to a known SHA256. This is what establishes
        provenance, and it covers every file in the archive.
      * WinDivert64.sys must carry a valid Authenticode signature. Windows will not
        load an unsigned driver, so failing here beats shipping an installer whose
        engine cannot start. The user mode WinDivert.dll is deliberately not
        required to be signed - upstream does not sign it, and it does not need to
        be for the loader to accept it.

    To move to a new upstream release, pass the new -Version together with the new
    -ExpectedSha256 (the script prints the hash it saw when they disagree).
#>
[CmdletBinding()]
param(
    [string]$Version = '2.2.2',

    # SHA256 of WinDivert-2.2.2-A.zip as published on the upstream GitHub release.
    [string]$ExpectedSha256 = '63CB41763BB4B20F600B6DE04E991A9C2BE73279E317D4D82F237B150C5F3F15',

    [string]$Destination = (Join-Path $PSScriptRoot '..\third-party\windivert')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$archive = "WinDivert-$Version-A.zip"
$sources = @(
    "https://github.com/basil00/WinDivert/releases/download/v$Version/$archive",
    "https://reqrypt.org/download/$archive"
)

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("windivert-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null
$zipPath = Join-Path $work $archive

try {
    $downloaded = $false
    foreach ($source in $sources) {
        try {
            Write-Host "Downloading $source"
            Invoke-WebRequest -Uri $source -OutFile $zipPath -UseBasicParsing -MaximumRedirection 5
            $downloaded = $true
            break
        }
        catch {
            Write-Warning "Failed: $($_.Exception.Message)"
        }
    }

    if (-not $downloaded) {
        throw "Could not download $archive from any known source."
    }

    $actualHash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash
    Write-Host "Archive SHA256: $actualHash"

    if ($ExpectedSha256 -and $actualHash -ne $ExpectedSha256.ToUpperInvariant()) {
        throw @"
$archive does not match the pinned hash.
  expected: $($ExpectedSha256.ToUpperInvariant())
  actual:   $actualHash
Refusing to bundle it. If this is an intentional upstream version change, pass the
new hash with -ExpectedSha256 after verifying it against the upstream release.
"@
    }

    Expand-Archive -Path $zipPath -DestinationPath $work -Force

    $dll = Get-ChildItem -Path $work -Recurse -Filter 'WinDivert.dll' |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Select-Object -First 1
    $sys = Get-ChildItem -Path $work -Recurse -Filter 'WinDivert64.sys' | Select-Object -First 1

    if (-not $dll -or -not $sys) {
        throw 'The archive did not contain the expected 64-bit WinDivert.dll and WinDivert64.sys.'
    }

    # The kernel refuses unsigned drivers, so this one is a hard requirement.
    $driverSignature = Get-AuthenticodeSignature -FilePath $sys.FullName
    if ($driverSignature.Status -ne 'Valid') {
        throw "WinDivert64.sys is not validly signed (status: $($driverSignature.Status)); Windows would refuse to load it."
    }

    Write-Host "WinDivert64.sys signer: $($driverSignature.SignerCertificate.Subject)"

    foreach ($file in @($dll, $sys)) {
        Write-Host "$($file.Name): SHA256 $((Get-FileHash -Path $file.FullName -Algorithm SHA256).Hash)"
    }

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Copy-Item -Path $dll.FullName -Destination (Join-Path $Destination 'WinDivert.dll') -Force
    Copy-Item -Path $sys.FullName -Destination (Join-Path $Destination 'WinDivert64.sys') -Force

    $license = Get-ChildItem -Path $work -Recurse -Include 'LICENSE*' | Select-Object -First 1
    if ($license) {
        Copy-Item -Path $license.FullName -Destination (Join-Path $Destination 'WinDivert-LICENSE.txt') -Force
    }

    Write-Host "WinDivert $Version ready in $Destination"
    Get-ChildItem -Path $Destination | Select-Object Name, Length | Format-Table | Out-String | Write-Host
}
finally {
    Remove-Item -Path $work -Recurse -Force -ErrorAction SilentlyContinue
}
