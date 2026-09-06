param(
    [Parameter(Mandatory = $true)][int]$TargetPid,
    [Parameter(Mandatory = $true)][string]$Name,
    [string[]]$CommandArgs = @(),
    [string]$OutFile,
    [int]$TimeoutMs = 15000
)

$ErrorActionPreference = 'Stop'

$pipeName = "BannerlordCoop.LiveTest.v1.$TargetPid"
$client = New-Object System.IO.Pipes.NamedPipeClientStream(
    '.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut)

try {
    $client.Connect(5000)

    $enc = New-Object System.Text.UTF8Encoding($false, $true)
    $reader = New-Object System.IO.StreamReader($client, $enc, $false, 65536, $true)
    $writer = New-Object System.IO.StreamWriter($client, $enc, 65536, $true)
    $writer.AutoFlush = $true

    # parameters keys are read literally by TryReadCommand, so they stay lowercase.
    $req = [ordered]@{
        version    = 1
        id         = [Guid]::NewGuid().ToString()
        method     = 'command'
        parameters = [ordered]@{
            name      = $Name
            arguments = @($CommandArgs)
        }
    }

    $json = ConvertTo-Json $req -Compress -Depth 8
    $writer.WriteLine($json)

    $readTask = $reader.ReadLineAsync()
    if (-not $readTask.Wait($TimeoutMs)) {
        Write-Output "TIMEOUT after $TimeoutMs ms waiting for pid $TargetPid"
        exit 2
    }

    $line = $readTask.Result
    if ($null -eq $line) {
        Write-Output "EMPTY response from pid $TargetPid"
        exit 3
    }

    if ($OutFile) {
        Set-Content -Path $OutFile -Value $line -Encoding utf8
        Write-Output "wrote $($line.Length) chars to $OutFile"
    }
    else {
        Write-Output $line
    }
}
finally {
    $client.Dispose()
}
