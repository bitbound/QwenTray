# Publishes QwenTray locally with the win-x64 publish profile, using the same
# date-based version scheme as the release workflow (UTC yyyy.M.d.HHmm, so two
# builds on the same day get distinct versions that System.Version sorts right).
[CmdletBinding()]
param(
    # Skip Authenticode signing. Needed when the CN=Bitbound cert is not in the
    # current user's Personal store (see README). The Sign CLI global tool is
    # required either way when signing.
    [switch]$NoSign
)

$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'QwenTray/QwenTray.csproj'
$profile = 'win-x64'
$version = (Get-Date).ToUniversalTime().ToString('yyyy.M.d.HHmm')

$publishArgs = @(
    'publish', $project
    '--configuration', 'Release'
    "-p:PublishProfile=$profile"
    "-p:Version=$version"
)

if ($NoSign) {
    $publishArgs += '-p:EnableSigning=false'
}

Write-Host "Publishing QwenTray $version with the $profile profile."
dotnet @publishArgs

$output = Join-Path $env:USERPROFILE 'Apps/QwenTray.exe'
if (Test-Path -LiteralPath $output) {
    Write-Host "Deployed to $output"
}
