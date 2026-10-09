# Downloads Microsoft's .NET Desktop Runtime installer and runs it, for the AI-Usage setup.
# Setup calls this only when the runtime is missing. The file that runs is the file that was
# checked, in this order:
#   1. The address is https and on a short list of Microsoft hosts, and so is every redirect hop.
#   2. The body goes into a folder created fresh for this run, under a random name, with a size cap.
#   3. The finished file is reopened and held open for reading (nobody can change, rename or delete
#      it) until the installer has ended.
#   4. With the file held, its Authenticode signature must be valid, embedded in the file, issued to
#      the organization "Microsoft Corporation" and chained to one of Microsoft's own root
#      certificates, with revocation checked online.
#   5. Only then the installer starts, with the "runas" verb, so Windows asks for permission once.
# Any failed step ends the run before the installer starts and leaves nothing behind. The result is
# the exit code: 0 = installed, anything else = failed (the log file says why).
param(
    [string]$Url,
    [string]$Folder,
    [string]$LogFile,
    # Checks one local file against the signature rules above and stops. For tests and manual checks.
    [string]$VerifyFile
)

$ErrorActionPreference = 'Stop'

# Exit codes besides 0.
$ExitBadUrl = 10
$ExitDownload = 11
$ExitSignature = 12
$ExitRun = 13
$ExitTimeout = 14
$ExitInstaller = 15

# Exact host names; a redirect anywhere else ends the download. The short link walks through
# exactly these two.
$AllowedHosts = @(
    'aka.ms',
    'builds.dotnet.microsoft.com'
)

# Microsoft's Root Certificate Authority 2010 and 2011: the roots the .NET installers chain to.
# Roots that also serve other publishers are left out on purpose. A later .NET installer that moves
# to another root needs that root added here deliberately.
$MicrosoftRoots = @(
    '3B1EFD3A66EA28B16697394703A72CA340A05BD5',
    '8F43288AD272F3103B6FB1428485EA3014C0BCFE'
)

$RequiredOrganization = 'Microsoft Corporation'
$MinBytes = 5MB
$MaxBytes = 250MB
$MaxRedirects = 5
$InstallLimitMinutes = 20

function Write-Log([string]$Message) {
    if ($script:LogFile) {
        Add-Content -LiteralPath $script:LogFile -Value ('{0:u} {1}' -f (Get-Date), $Message) -ErrorAction SilentlyContinue
    }
}

function Test-AllowedUrl([string]$Address) {
    $uri = $null
    if (-not [Uri]::TryCreate($Address, [UriKind]::Absolute, [ref]$uri)) { return $false }
    if ($uri.Scheme -ne 'https') { return $false }
    if ($uri.Port -ne 443) { return $false }
    if ($uri.UserInfo) { return $false }
    return ($AllowedHosts -contains $uri.Host.ToLowerInvariant())
}

# Splits a certificate subject into (type, value) pairs. Quotes are honored, so a value that merely
# contains ", O=Microsoft Corporation" inside quotes is not mistaken for a field of its own.
function Get-SubjectFields([string]$Subject) {
    $fields = New-Object System.Collections.Generic.List[object]
    $current = New-Object System.Text.StringBuilder
    $inQuote = $false
    $parts = New-Object System.Collections.Generic.List[string]
    for ($i = 0; $i -lt $Subject.Length; $i++) {
        $c = $Subject[$i]
        if ($c -eq '"') {
            if ($inQuote -and ($i + 1) -lt $Subject.Length -and $Subject[$i + 1] -eq '"') {
                [void]$current.Append('""')
                $i++
                continue
            }
            $inQuote = -not $inQuote
            [void]$current.Append($c)
        }
        elseif ($c -eq ',' -and -not $inQuote) {
            $parts.Add($current.ToString())
            [void]$current.Clear()
        }
        else {
            [void]$current.Append($c)
        }
    }
    $parts.Add($current.ToString())

    foreach ($part in $parts) {
        $eq = $part.IndexOf('=')
        if ($eq -lt 1) { continue }
        $type = $part.Substring(0, $eq).Trim().ToUpperInvariant()
        $value = $part.Substring($eq + 1).Trim()
        if ($value.Length -ge 2 -and $value.StartsWith('"') -and $value.EndsWith('"')) {
            $value = $value.Substring(1, $value.Length - 2).Replace('""', '"')
        }
        $fields.Add([pscustomobject]@{ Type = $type; Value = $value })
    }
    return $fields.ToArray()
}

function Test-MicrosoftSubject([string]$Subject) {
    $organizations = @(Get-SubjectFields $Subject | Where-Object { $_.Type -eq 'O' })
    return ($organizations.Count -eq 1 -and $organizations[0].Value -ceq $RequiredOrganization)
}

# True for the product name and version of the .NET 10 Desktop Runtime installer, for example
# "Microsoft Windows Desktop Runtime 10.0.12 (x64)" (older releases wrote " - " before the version)
# with product version 10.0.12.xxxxx.
function Test-RuntimeProduct([string]$ProductName, [string]$ProductVersion) {
    if ($ProductName -notmatch '^Microsoft Windows Desktop Runtime (- )?10\.\d+\.\d+\S* \(\w+\)$') { return $false }
    return ($ProductVersion -match '^10\.\d+\.\d+')
}

# Returns $null when the file is the .NET 10 Desktop Runtime installer, otherwise the reason it is
# not. A valid Microsoft signature alone says nothing about which Microsoft program this is.
function Get-ProductProblem([string]$Path) {
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    if (-not (Test-RuntimeProduct $info.ProductName $info.ProductVersion)) {
        return "file is not the .NET 10 Desktop Runtime installer (product '$($info.ProductName)' $($info.ProductVersion))"
    }
    return $null
}

# Returns $null when the file is Microsoft's, otherwise the reason it is not.
function Get-SignatureProblem([string]$Path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid') { return "signature status is $($signature.Status)" }
    if ($signature.SignatureType -ne 'Authenticode') { return 'the signature is not embedded in the file' }

    $certificate = $signature.SignerCertificate
    if (-not $certificate) { return 'no signer certificate' }
    if (-not (Test-MicrosoftSubject $certificate.Subject)) { return "signer is not $RequiredOrganization" }

    $chain = New-Object System.Security.Cryptography.X509Certificates.X509Chain
    try {
        $chain.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::Online
        $chain.ChainPolicy.RevocationFlag = [System.Security.Cryptography.X509Certificates.X509RevocationFlag]::EntireChain
        [void]$chain.ChainPolicy.ApplicationPolicy.Add([System.Security.Cryptography.Oid]'1.3.6.1.5.5.7.3.3')
        $built = $chain.Build($certificate)
        # An expired signer certificate is fine when the signature carries a trusted time stamp;
        # Get-AuthenticodeSignature has already judged that above. Everything else is not.
        $blocking = @($chain.ChainStatus | Where-Object { $_.Status -ne [System.Security.Cryptography.X509Certificates.X509ChainStatusFlags]::NotTimeValid })
        if ((-not $built) -and $blocking.Count -gt 0) {
            return "certificate chain: $($blocking[0].Status)"
        }
        if ($chain.ChainElements.Count -lt 2) { return 'certificate chain is too short' }
        $root = $chain.ChainElements[$chain.ChainElements.Count - 1].Certificate
        if ($MicrosoftRoots -notcontains $root.Thumbprint.ToUpperInvariant()) { return 'chain does not end at a Microsoft root' }
    }
    finally {
        $chain.Dispose()
    }
    return $null
}

function Save-Download([string]$Address, [string]$Destination) {
    Add-Type -AssemblyName System.Net.Http
    $protocols = [Net.SecurityProtocolType]::Tls12
    try { $protocols = $protocols -bor [Net.SecurityProtocolType]12288 } catch { }
    [Net.ServicePointManager]::SecurityProtocol = $protocols

    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.AllowAutoRedirect = $false
    $handler.DefaultProxyCredentials = [Net.CredentialCache]::DefaultCredentials
    $client = New-Object System.Net.Http.HttpClient($handler)
    $client.Timeout = [TimeSpan]::FromMinutes(5)
    try {
        $current = [Uri]$Address
        for ($hop = 0; $hop -le $MaxRedirects; $hop++) {
            if (-not (Test-AllowedUrl $current.AbsoluteUri)) { throw "address is not on the allow-list: $($current.Host)" }
            $response = $client.GetAsync($current, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
            try {
                $status = [int]$response.StatusCode
                if ($status -in 301, 302, 303, 307, 308 -and $response.Headers.Location) {
                    $next = $response.Headers.Location
                    if (-not $next.IsAbsoluteUri) { $next = New-Object Uri($current, $next) }
                    $current = $next
                    continue
                }
                if ($status -ne 200) { throw "server answered $status" }

                $length = $response.Content.Headers.ContentLength
                if ($length -and $length -gt $MaxBytes) { throw 'download is larger than the installer can be' }

                $source = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                # CreateNew: the name is random inside a folder made for this run; an existing file
                # of that name is a failure, never overwritten or reused.
                $target = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                try {
                    $buffer = New-Object byte[] 81920
                    $received = 0L
                    while (($read = $source.Read($buffer, 0, $buffer.Length)) -gt 0) {
                        $received += $read
                        if ($received -gt $MaxBytes) { throw 'download is larger than the installer can be' }
                        $target.Write($buffer, 0, $read)
                    }
                }
                finally {
                    $target.Dispose()
                    $source.Dispose()
                }
                if ($received -lt $MinBytes) { throw "download is only $received bytes" }
                return
            }
            finally {
                $response.Dispose()
            }
        }
        throw 'too many redirects'
    }
    finally {
        $client.Dispose()
        $handler.Dispose()
    }
}

function Invoke-Main {
    if ($VerifyFile) {
        $guard = $null
        try {
            $guard = [IO.File]::Open($VerifyFile, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            $problem = Get-SignatureProblem $VerifyFile
            if (-not $problem) { $problem = Get-ProductProblem $VerifyFile }
        }
        catch {
            $problem = "file could not be checked: $($_.Exception.Message)"
        }
        finally {
            if ($guard) { $guard.Dispose() }
        }
        if ($problem) {
            Write-Log "verify: $problem"
            [Console]::Out.WriteLine($problem)
            return $ExitSignature
        }
        return 0
    }

    if (-not $Url -or -not $Folder) { Write-Log 'missing parameters'; return $ExitBadUrl }
    if (-not (Test-AllowedUrl $Url)) { Write-Log "address refused: $Url"; return $ExitBadUrl }

    $work = $null
    $guard = $null
    try {
        # A folder that did not exist a moment ago, named at random: nothing can be waiting in it.
        $work = Join-Path $Folder ('runtime-' + [Guid]::NewGuid().ToString('N'))
        [void][IO.Directory]::CreateDirectory($work)
        $file = Join-Path $work 'windowsdesktop-runtime.exe'

        try {
            Write-Log "download: $Url"
            Save-Download $Url $file
        }
        catch {
            Write-Log "download failed: $($_.Exception.Message)"
            return $ExitDownload
        }

        # Held from here to the end: the checked file is the file that runs.
        $guard = [IO.File]::Open($file, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)

        $problem = Get-SignatureProblem $file
        if (-not $problem) { $problem = Get-ProductProblem $file }
        if ($problem) {
            Write-Log "installer refused: $problem"
            return $ExitSignature
        }
        Write-Log 'signature and product accepted'

        try {
            $process = Start-Process -FilePath $file -ArgumentList '/install', '/quiet', '/norestart' -Verb RunAs -PassThru
        }
        catch {
            Write-Log "installer did not start: $($_.Exception.Message)"
            return $ExitRun
        }

        if (-not $process.WaitForExit($InstallLimitMinutes * 60 * 1000)) {
            Write-Log 'installer did not finish in time'
            return $ExitTimeout
        }
        $code = $process.ExitCode
        Write-Log "installer exit code $code"
        # 3010 and 1641 mean installed, restart wanted; 1638 means a newer build is already there.
        # Setup checks the registry afterwards, which is what counts.
        if ($code -in 0, 3010, 1641, 1638) { return 0 }
        return $ExitInstaller
    }
    catch {
        Write-Log "failed: $($_.Exception.Message)"
        return $ExitRun
    }
    finally {
        if ($guard) { $guard.Dispose() }
        if ($work -and (Test-Path -LiteralPath $work)) {
            Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

# Dot-sourcing (for tests) loads the functions without running anything.
if ($MyInvocation.InvocationName -ne '.') {
    exit ([int](@(Invoke-Main))[-1])
}
