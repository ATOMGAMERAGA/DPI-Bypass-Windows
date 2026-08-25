<#
.SYNOPSIS
    Checks the decisions scripts/install.ps1 makes before it touches the machine.

.DESCRIPTION
    The one line installer is also the updater: running it again has to install a
    newer release, say so and stop when the newest one is already there, and never
    tear down a working installation for nothing. That logic lives in two pure
    functions, and this pulls them out of the script - without running the script -
    and puts every case through them.

    Run it with:
      pwsh -File scripts/tests/install.tests.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path (Split-Path -Parent (Split-Path -Parent $PSCommandPath)) 'install.ps1'
if (-not (Test-Path $scriptPath)) {
    throw "install.ps1 not found at $scriptPath"
}

# Parse rather than dot-source: the script installs things when it runs.
$errors = $null
$tokens = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors)

if ($errors -and $errors.Count -gt 0) {
    foreach ($parseError in $errors) {
        Write-Host "install.ps1($($parseError.Extent.StartLineNumber)): $($parseError.Message)" -ForegroundColor Red
    }

    throw 'install.ps1 does not parse.'
}

foreach ($name in @('ConvertTo-ComparableVersion', 'Get-UpdateDecision', 'Get-InstalledRelease')) {
    $definition = $ast.Find(
        [Func[System.Management.Automation.Language.Ast, bool]] {
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
        },
        $true)

    if (-not $definition) {
        throw "install.ps1 no longer defines $name"
    }

    # Bring the function into this session exactly as the script declares it.
    . ([scriptblock]::Create($definition.Extent.Text))
}

$failures = New-Object System.Collections.Generic.List[string]

function Test-Case([string]$Name, [scriptblock]$Body) {
    try {
        & $Body
        Write-Host "  ok   $Name" -ForegroundColor Green
    }
    catch {
        Write-Host "  FAIL $Name" -ForegroundColor Red
        Write-Host "       $($_.Exception.Message)" -ForegroundColor Red
        $script:failures.Add($Name)
    }
}

function Assert-Equal($Expected, $Actual, [string]$Because) {
    if ("$Expected" -ne "$Actual") {
        throw "expected '$Expected' but got '$Actual'$(if ($Because) { " - $Because" })"
    }
}

Write-Host 'install.ps1' -ForegroundColor Cyan

Test-Case 'a tag with the v prefix parses' {
    Assert-Equal ([version]'1.0.0.28') (ConvertTo-ComparableVersion 'v1.0.0.28')
}

Test-Case 'a bare version parses' {
    Assert-Equal ([version]'1.0.0.28') (ConvertTo-ComparableVersion '1.0.0.28')
}

Test-Case 'a three part version parses' {
    Assert-Equal ([version]'1.2.3') (ConvertTo-ComparableVersion '1.2.3')
}

Test-Case 'trailing text is ignored' {
    Assert-Equal ([version]'1.0.0.28') (ConvertTo-ComparableVersion '1.0.0.28-beta')
}

Test-Case 'nonsense is not a version' {
    Assert-Equal $null (ConvertTo-ComparableVersion 'sürüm yok')
}

Test-Case 'nothing is not a version' {
    Assert-Equal $null (ConvertTo-ComparableVersion '')
}

Test-Case 'the same version installed means there is nothing to do' {
    $decision = Get-UpdateDecision -InstalledVersion '1.0.0.28' -LatestVersion 'v1.0.0.28'
    Assert-Equal 'up-to-date' $decision.Action
}

Test-Case 'a newer release is an update' {
    $decision = Get-UpdateDecision -InstalledVersion '1.0.0.25' -LatestVersion 'v1.0.0.28'
    Assert-Equal 'update' $decision.Action
}

Test-Case 'the build number alone is enough to see an update' {
    $decision = Get-UpdateDecision -InstalledVersion '1.0.0.28' -LatestVersion 'v1.0.0.29'
    Assert-Equal 'update' $decision.Action
}

Test-Case 'a locally built copy ahead of the release is left alone' {
    $decision = Get-UpdateDecision -InstalledVersion '1.1.0.0' -LatestVersion 'v1.0.0.28'
    Assert-Equal 'newer-installed' $decision.Action
}

Test-Case 'nothing installed means install' {
    $decision = Get-UpdateDecision -InstalledVersion '' -LatestVersion 'v1.0.0.28'
    Assert-Equal 'install' $decision.Action
}

Test-Case 'an unreadable installed version means install rather than refuse' {
    $decision = Get-UpdateDecision -InstalledVersion 'bilinmiyor' -LatestVersion 'v1.0.0.28'
    Assert-Equal 'install' $decision.Action
}

Test-Case 'force reinstalls the version that is already there' {
    $decision = Get-UpdateDecision -InstalledVersion '1.0.0.28' -LatestVersion 'v1.0.0.28' -Force
    Assert-Equal 'install' $decision.Action
}

Test-Case 'version ordering is numeric, not alphabetical' {
    # "1.0.0.9" sorts after "1.0.0.10" as text, which would hide every tenth release.
    $decision = Get-UpdateDecision -InstalledVersion '1.0.0.9' -LatestVersion 'v1.0.0.10'
    Assert-Equal 'update' $decision.Action
}

Test-Case 'the decision carries the versions it compared' {
    $decision = Get-UpdateDecision -InstalledVersion '1.0.0.25' -LatestVersion 'v1.0.0.28'
    Assert-Equal ([version]'1.0.0.25') $decision.Installed
    Assert-Equal ([version]'1.0.0.28') $decision.Latest
}

Test-Case 'nothing installed is reported as nothing installed' {
    # Runs against this machine: on a build agent there is no DPI Bypass, and the
    # function has to say so rather than throw.
    $installed = Get-InstalledRelease
    if ($null -ne $installed -and -not $installed.RegistryPath) {
        throw 'an installation was reported without a registry path'
    }
}

Write-Host ''

if ($failures.Count -gt 0) {
    Write-Host "$($failures.Count) test failed." -ForegroundColor Red
    exit 1
}

Write-Host 'All install script tests passed.' -ForegroundColor Green

# Said out loud, because a script that just ends leaves $LASTEXITCODE at whatever
# the caller had before it - which is not an answer.
exit 0
