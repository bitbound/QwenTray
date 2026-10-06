# Signs the bundled binaries that do not carry a signature yet, using the SHA-256
# fingerprint that Find-SigningCert.ps1 resolved. The Sign CLI overwrites its
# inputs and offers no skip option, so files that are already signed (the
# Microsoft-signed runtime assemblies) are filtered out first. The remaining files
# are passed positionally in batches, because the CLI signs only its positional
# arguments and its -fl option is a glob filter, not a file source.
param(
    [Parameter(Mandatory)][string]$FileList,
    [Parameter(Mandatory)][string]$BaseDirectory,
    [Parameter(Mandatory)][string]$Fingerprint,
    [Parameter(Mandatory)][string]$SignTool,
    [string]$Description = '',
    [string]$DescriptionUrl = '',
    [string]$TimestampUrl = ''
)

$ErrorActionPreference = 'Stop'

# Windows keeps an Authenticode signature in the PE certificate table, and the
# Security data directory records its size. Reading that avoids Get-AuthenticodeSignature,
# whose module fails to load in the shell MSBuild spawns.
function Test-FileSigned([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $reader = New-Object IO.BinaryReader($stream)
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) { return $false }
        $stream.Position = $peOffset + 24
        $magic = $reader.ReadUInt16()
        $dataDirectories = if ($magic -eq 0x20B) { $peOffset + 24 + 112 } else { $peOffset + 24 + 96 }
        $stream.Position = $dataDirectories + 32
        $null = $reader.ReadUInt32()
        return ($reader.ReadUInt32() -gt 0)
    }
    finally {
        $stream.Dispose()
    }
}

$paths = @(foreach ($entry in (Get-Content -LiteralPath $FileList)) {
    if ([string]::IsNullOrWhiteSpace($entry)) { continue }
    $path = if ([IO.Path]::IsPathRooted($entry)) { $entry } else { Join-Path $BaseDirectory $entry }
    (Resolve-Path -LiteralPath $path).Path
})

$unsigned = @($paths | Where-Object { -not (Test-FileSigned $_) })

if ($unsigned.Count -eq 0) {
    Write-Output "All $($paths.Count) bundled binaries are already signed."
    exit 0
}

$signArgs = @('code', 'certificate-store', '-cfp', $Fingerprint, '-fd', 'SHA256')
if ($Description) { $signArgs += @('-d', $Description) }
if ($DescriptionUrl) { $signArgs += @('-u', $DescriptionUrl) }
if ($TimestampUrl) { $signArgs += @('-t', $TimestampUrl, '-td', 'SHA256') }

# Batches keep the command line under the Windows length limit.
$batchSize = 50
for ($offset = 0; $offset -lt $unsigned.Count; $offset += $batchSize) {
    $batch = $unsigned[$offset..([Math]::Min($offset + $batchSize - 1, $unsigned.Count - 1))]
    & $SignTool @signArgs @batch
    if ($LASTEXITCODE -ne 0) {
        throw "The Sign CLI failed for a batch of bundled binaries with exit code $LASTEXITCODE."
    }
}

Write-Output "Signed $($unsigned.Count) of $($paths.Count) bundled binaries."
