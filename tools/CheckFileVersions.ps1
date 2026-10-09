# Fails (exit 1) unless the built exe and every built setup in -Dist carry the csproj version in their
# file version resource. The in-app update reads that resource to decide whether a download is newer,
# so a build that ships a different number would make every update look old.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Dist
)

$ErrorActionPreference = 'Stop'

function ConvertTo-FourParts([version]$v) {
    [version]::new($v.Major, $v.Minor, [Math]::Max($v.Build, 0), [Math]::Max($v.Revision, 0))
}

try {
    $expected = ConvertTo-FourParts ([version]$Version)

    $files = @(Join-Path $Dist 'AI-Usage.exe'; Join-Path $Dist 'AI-Usage-arm64.exe')
    $files += @(Get-ChildItem -Path $Dist -Filter 'Setup-AI-Usage-*.exe' -File | ForEach-Object { $_.FullName })

    $report = @()
    foreach ($file in $files) {
        if (-not (Test-Path -LiteralPath $file)) {
            throw "missing build output $(Split-Path -Leaf $file)"
        }

        $raw = (Get-Item -LiteralPath $file).VersionInfo.FileVersion
        $parsed = $null
        if ([string]::IsNullOrWhiteSpace($raw) -or -not [version]::TryParse($raw, [ref]$parsed)) {
            throw "$(Split-Path -Leaf $file) has no readable file version (found '$raw')"
        }

        $actual = ConvertTo-FourParts $parsed
        if ($actual -ne $expected) {
            throw "$(Split-Path -Leaf $file) has file version $actual, the csproj version is $expected"
        }

        $report += "$(Split-Path -Leaf $file) $actual"
    }

    Write-Host ("File versions match the csproj version {0}: {1}" -f $expected, ($report -join ', '))
    exit 0
}
catch {
    Write-Host "ERROR: file version check failed: $($_.Exception.Message)"
    exit 1
}
