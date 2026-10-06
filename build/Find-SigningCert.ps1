# Prints the SHA-256 fingerprint of the code-signing certificate whose subject
# matches $Subject (newest first) in the current user's Personal store. Exits 1
# if no such certificate with a private key exists. The MSBuild signing targets
# feed this fingerprint to the Sign CLI.
param(
    [string]$Subject = 'CN=Bitbound'
)

$ErrorActionPreference = 'Stop'

# Use the .NET store API instead of the Cert: PSDrive; the drive isn't always
# available in non-interactive or nested shells (e.g. spawned from MSBuild).
$store = New-Object System.Security.Cryptography.X509Certificates.X509Store('My', 'CurrentUser')
try {
    $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
    $cert = $store.Certificates |
        Where-Object { $_.Subject -eq $Subject -and $_.HasPrivateKey } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
}
finally {
    $store.Close()
}

if (-not $cert) {
    exit 1
}

$sha256 = [System.Security.Cryptography.SHA256]::Create().ComputeHash($cert.RawData)
[System.BitConverter]::ToString($sha256).Replace('-', '')
