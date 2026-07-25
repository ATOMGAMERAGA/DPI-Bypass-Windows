<#
.SYNOPSIS
    Downloads the WinDivert driver payload that ships with Atom DPI Bypass.

.DESCRIPTION
    The 64-bit WinDivert.dll and WinDivert64.sys are placed in third-party/windivert
    so the app project copies them next to the executable.

    The download is verified by Authenticode signature rather than by a pinned
    hash: the driver has to be signed for Windows to load it at all, so a valid
    signature from the expected publisher is both a stronger and a more durable
    check than a hash that would have to be bumped for every upstream release.
#>
[CmdletBinding()]
param(
    [string]$Version = '2.2.2',
    [string]$Destination = (Join-Path $PSScriptRoot '..\third-party\windivert'),
    [string]$ExpectedSubject = 'Basil'
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

Write-Host "Archive SHA256: $((Get-FileHash -Path $zipPath -Algorithm SHA256).Hash)"

Expand-Archive -Path $zipPath -DestinationPath $work -Force

$dll = Get-ChildItem -Path $work -Recurse -Filter 'WinDivert.dll' |
    Where-Object { $_.FullName -match '\\x64\\' } |
    Select-Object -First 1
$sys = Get-ChildItem -Path $work -Recurse -Filter 'WinDivert64.sys' | Select-Object -First 1

if (-not $dll -or -not $sys) {
    throw 'The archive did not contain the expected 64-bit WinDivert.dll and WinDivert64.sys.'
}

foreach ($file in @($dll, $sys)) {
    $signature = Get-AuthenticodeSignature -FilePath $file.FullName

    if ($signature.Status -ne 'Valid') {
        throw "$($file.Name) is not validly signed (status: $($signature.Status))."
    }

    if ($signature.SignerCertificate.Subject -notmatch $ExpectedSubject) {
        throw "$($file.Name) is signed by an unexpected publisher: $($signature.SignerCertificate.Subject)"
    }

    Write-Host "$($file.Name): signature valid, SHA256 $((Get-FileHash -Path $file.FullName -Algorithm SHA256).Hash)"
}

New-Item -ItemType Directory -Path $Destination -Force | Out-Null
Copy-Item -Path $dll.FullName -Destination (Join-Path $Destination 'WinDivert.dll') -Force
Copy-Item -Path $sys.FullName -Destination (Join-Path $Destination 'WinDivert64.sys') -Force

$license = Get-ChildItem -Path $work -Recurse -Include 'LICENSE*' | Select-Object -First 1
if ($license) {
    Copy-Item -Path $license.FullName -Destination (Join-Path $Destination 'WinDivert-LICENSE.txt') -Force
}

Remove-Item -Path $work -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "WinDivert $Version ready in $Destination"
Get-ChildItem -Path $Destination | Select-Object Name, Length | Format-Table | Out-String | Write-Host
