<#
.SYNOPSIS
    Checks that every resource the window asks for actually exists.

.DESCRIPTION
    A StaticResource whose key is not defined throws while the window is being
    built. That happens before anything is on screen, so what the user sees is an
    app that starts and then is not there - the failure this project keeps having
    to chase. The compiler does not catch it, because resource lookup happens at
    run time, so it is checked here instead.

    Run it with:
      pwsh -File scripts/tests/xaml-resources.tests.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
$appDirectory = Join-Path $root 'src/DpiBypass.App'
$themeDirectory = Join-Path $appDirectory 'Theme'

if (-not (Test-Path $themeDirectory)) {
    throw "Theme dictionaries not found at $themeDirectory"
}

# Keys the palettes and the shared dictionary define, plus the ones WPF itself
# ships (SystemColors and friends are resolved by the framework, not by us).
$defined = New-Object System.Collections.Generic.HashSet[string]
foreach ($file in Get-ChildItem $appDirectory -Filter '*.xaml' -Recurse) {
    $text = Get-Content -Path $file.FullName -Raw
    foreach ($match in [regex]::Matches($text, 'x:Key="([^"]+)"')) {
        [void]$defined.Add($match.Groups[1].Value)
    }
}

Write-Host 'XAML resources' -ForegroundColor Cyan
Write-Host "  $($defined.Count) key(s) defined across the app's dictionaries"

$missing = New-Object System.Collections.Generic.List[string]
$forwardReferences = New-Object System.Collections.Generic.List[string]
$references = 0

foreach ($file in Get-ChildItem $appDirectory -Filter '*.xaml' -Recurse) {
    $text = Get-Content -Path $file.FullName -Raw

    foreach ($match in [regex]::Matches($text, '\{(StaticResource|DynamicResource)\s+([^},]+)\}')) {
        $kind = $match.Groups[1].Value
        $key = $match.Groups[2].Value.Trim()
        $references++

        # A key with a dot in it is a framework resource (SystemColors.*, the
        # Fluent theme's own brushes); those are not ours to define.
        if ($key.Contains('.')) { continue }

        if (-not $defined.Contains($key)) {
            $missing.Add("$($file.Name): $kind $key")
        }
    }

    # StaticResource is resolved while a ResourceDictionary is read and therefore
    # cannot point forward to a key declared later in that same dictionary. Merely
    # checking that the key exists somewhere missed exactly this failure: the app
    # compiled, but MainWindow.InitializeComponent threw before drawing a frame.
    if ($text -match '^\s*<ResourceDictionary\b') {
        $definitions = @{}
        foreach ($definition in [regex]::Matches($text, 'x:Key="([^"]+)"')) {
            if (-not $definitions.ContainsKey($definition.Groups[1].Value)) {
                $definitions[$definition.Groups[1].Value] = $definition.Index
            }
        }

        foreach ($reference in [regex]::Matches($text, '\{StaticResource\s+([^},]+)\}')) {
            $key = $reference.Groups[1].Value.Trim()
            if ($definitions.ContainsKey($key) -and $definitions[$key] -gt $reference.Index) {
                $line = 1 + $text.Substring(0, $reference.Index).Split("`n").Count - 1
                $forwardReferences.Add("$($file.Name):$line StaticResource $key is declared later")
            }
        }
    }
}

Write-Host "  $references reference(s) checked"

if ($missing.Count -gt 0 -or $forwardReferences.Count -gt 0) {
    Write-Host ''
    foreach ($entry in $missing) {
        Write-Host "  MISSING $entry" -ForegroundColor Red
    }
    foreach ($entry in $forwardReferences) {
        Write-Host "  FORWARD $entry" -ForegroundColor Red
    }

    Write-Host ''
    Write-Host "$($missing.Count) missing and $($forwardReferences.Count) forward resource reference(s)." -ForegroundColor Red
    exit 1
}

# The palettes have to agree with each other, or switching to the theme that is
# missing a key takes the window down with it the moment Windows changes.
$paletteKeys = @{}
foreach ($file in @('Light.xaml', 'Dark.xaml')) {
    $path = Join-Path $themeDirectory $file
    if (-not (Test-Path $path)) { throw "$file is missing" }

    $text = Get-Content -Path $path -Raw
    $keys = New-Object System.Collections.Generic.HashSet[string]
    foreach ($match in [regex]::Matches($text, 'x:Key="([^"]+)"')) {
        [void]$keys.Add($match.Groups[1].Value)
    }

    $paletteKeys[$file] = $keys
}

$onlyInLight = @($paletteKeys['Light.xaml'] | Where-Object { -not $paletteKeys['Dark.xaml'].Contains($_) })
$onlyInDark = @($paletteKeys['Dark.xaml'] | Where-Object { -not $paletteKeys['Light.xaml'].Contains($_) })

if ($onlyInLight.Count -gt 0 -or $onlyInDark.Count -gt 0) {
    if ($onlyInLight.Count -gt 0) { Write-Host "  only in Light.xaml: $($onlyInLight -join ', ')" -ForegroundColor Red }
    if ($onlyInDark.Count -gt 0) { Write-Host "  only in Dark.xaml: $($onlyInDark -join ', ')" -ForegroundColor Red }
    Write-Host 'The two palettes do not define the same keys.' -ForegroundColor Red
    exit 1
}

Write-Host "  light and dark palettes define the same $($paletteKeys['Light.xaml'].Count) key(s)"
Write-Host ''
Write-Host 'All XAML resource tests passed.' -ForegroundColor Green

# As above: success has to be an exit code the caller can read.
exit 0
