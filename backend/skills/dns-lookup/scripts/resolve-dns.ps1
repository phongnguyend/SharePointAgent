param(
    [Parameter(Mandatory = $true)]
    [string]$Domain,

    [ValidateSet('A', 'AAAA', 'MX', 'TXT', 'NS', 'CNAME')]
    [string]$RecordType = 'A',

    [string]$DnsServer
)

$ErrorActionPreference = 'Stop'
try {
    if ($Domain -notmatch '^(?=.{1,254}$)(?:[a-zA-Z0-9_](?:[a-zA-Z0-9_-]{0,61}[a-zA-Z0-9_])?\.)*[a-zA-Z0-9_](?:[a-zA-Z0-9_-]{0,61}[a-zA-Z0-9_])?\.?$') {
        throw 'Invalid domain format. Supply a DNS name, not a URL or command.'
    }
    $serverAddress = $null
    if ($DnsServer -and -not [System.Net.IPAddress]::TryParse($DnsServer, [ref]$serverAddress)) {
        throw 'Invalid DNS server format. Must be an IPv4 or IPv6 address.'
    }
    $RecordType = $RecordType.ToUpperInvariant()
    $records = @()
    if ($env:OS -eq 'Windows_NT') {
        $query = @{ Name = $Domain; Type = $RecordType; DnsOnly = $true; ErrorAction = 'Stop' }
        if ($DnsServer) {
            $query.Server = $serverAddress.ToString()
        }
        $records = @(Resolve-DnsName @query | Where-Object { $_.Section -eq 'Answer' } | ForEach-Object {
            $answerRecord = $_
            $typeName = $answerRecord.Type.ToString()
            $record = [ordered]@{ Name = $_.Name.TrimEnd('.'); Type = $typeName; TTL = [long]$_.TTL }
            switch ($typeName) {
                { $_ -in 'A', 'AAAA' } {
                    $record.IPAddress = $answerRecord.IPAddress
                }
                { $_ -in 'CNAME', 'NS' } {
                    $record.NameHost = $answerRecord.NameHost.TrimEnd('.')
                }
                'MX' {
                    $record.Exchange = $answerRecord.NameExchange
                    $record.Preference = [int]$answerRecord.Preference
                }
                'TXT' {
                    $record.Text = $answerRecord.Strings -join ''
                }
                default {
                    return
                }
            }
            [PSCustomObject]$record
        })
    }
    else {
        $dig = Get-Command dig -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $dig) {
            throw 'dig is required on Linux. Install dnsutils (Debian/Ubuntu) or bind-utils.'
        }
        # Ignore .digrc and pass separate arguments without shell interpretation.
        $digArguments = @('-r', '+noall', '+answer', '+comments', '+nomultiline', '+time=5', '+tries=1', '-q', $Domain, '-t', $RecordType)
        if ($DnsServer) {
            $digArguments += '@' + $serverAddress.ToString()
        }
        $lines = @(& $dig.Source @digArguments 2>&1)
        if ($LASTEXITCODE -ne 0) {
            throw "dig failed (exit $LASTEXITCODE): $($lines -join ' ')"
        }
        $response = $lines -join "`n"
        if ($response -notmatch 'status:\s*(\w+)') {
            throw 'DNS response did not include a status.'
        }
        if ($Matches[1] -ne 'NOERROR') {
            throw "DNS server returned $($Matches[1])."
        }
        $records = @(foreach ($line in $lines) {
            if ($line -notmatch '^(\S+)\s+(\d+)\s+IN\s+(A|AAAA|MX|TXT|NS|CNAME)\s+(.+)$') {
                continue
            }
            $typeName = $Matches[3]
            $data = $Matches[4]
            $record = [ordered]@{ Name = $Matches[1].TrimEnd('.'); Type = $typeName; TTL = [long]$Matches[2] }
            switch ($typeName) {
                { $_ -in 'A', 'AAAA' } {
                    $record.IPAddress = $data.Trim()
                }
                { $_ -in 'CNAME', 'NS' } {
                    $record.NameHost = $data.Trim().TrimEnd('.')
                }
                'MX' {
                    $parts = $data -split '\s+', 2
                    $record.Preference = [int]$parts[0]
                    $record.Exchange = $parts[1]
                }
                'TXT' {
                    # Decode dig's quoted strings and decimal byte escapes.
                    $segments = [regex]::Matches($data, '"((?:[^"\\]|\\.)*)"')
                    $record.Text = ($segments | ForEach-Object {
                        [regex]::Replace($_.Groups[1].Value, '\\(\d{3}|.)', {
                            param($escape)
                            if ($escape.Groups[1].Value -match '^\d{3}$') {
                                return [string][char][int]$escape.Groups[1].Value
                            }
                            return $escape.Groups[1].Value
                        })
                    }) -join ''
                }
            }
            [PSCustomObject]$record
        })
    }
    # Always emit an array, including zero or one record.
    ConvertTo-Json -InputObject @($records) -Depth 5 -Compress
}
catch {
    [Console]::Error.WriteLine("DNS query failed: $($_.Exception.Message)")
    exit 1
}
