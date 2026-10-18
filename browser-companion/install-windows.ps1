[CmdletBinding(DefaultParameterSetName='Install')]
param(
    [Parameter(Mandatory=$true,ParameterSetName='Install')][string]$HostExecutable,
    [Parameter(Mandatory=$true,ParameterSetName='Remove')][switch]$Uninstall
)
$ErrorActionPreference='Stop'
$name='net.atom.dpibypass.social'
$keys=@("HKCU:\Software\Google\Chrome\NativeMessagingHosts\$name", "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\$name")
$manifestDirectory=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'DPI Bypass/browser-companion'
$manifestPath=Join-Path $manifestDirectory "$name.json"
if($Uninstall) {
    foreach($key in $keys) { if(Test-Path -LiteralPath $key) { Remove-Item -LiteralPath $key } }
    if(Test-Path -LiteralPath $manifestPath) { Remove-Item -LiteralPath $manifestPath }
    exit
}
$executable=(Resolve-Path -LiteralPath $HostExecutable).Path
if([IO.Path]::GetFileName($executable) -ne 'DpiBypass.SocialHost.exe') { throw 'Select the published DpiBypass.SocialHost.exe' }
New-Item -ItemType Directory -Path $manifestDirectory -Force | Out-Null
@{name=$name;description='DPI Bypass local Social Observer';path=$executable;type='stdio';allowed_origins=@('chrome-extension://figpmfdodbgjjdhlfadcdkigimbbelll/')} |
    ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding utf8
foreach($key in $keys) { New-Item -Path $key -Force | Out-Null; Set-Item -LiteralPath $key -Value $manifestPath }
Write-Output 'Registered for this user. Load this folder as an unpacked Chromium extension; enable it explicitly.'
