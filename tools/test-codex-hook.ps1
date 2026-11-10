param([switch]$PowerShellShell)
# Tests both Windows shells supported by Codex's runtime.
# Uses an isolated TCP port, so no synthetic events reach the live Sender.
$ErrorActionPreference = 'Stop'
$codexDir = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE '.codex' }
$config = Get-Content (Join-Path $codexDir 'hooks.json') -Raw | ConvertFrom-Json
$hook = @($config.hooks.PreToolUse | ForEach-Object { $_.hooks } |
    Where-Object { $_.command -like '*peemo-codex-hook.ps1*' })
if ($hook.Count -ne 1) { throw 'Expected exactly one installed Peemo PreToolUse hook.' }
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$process = $null
try {
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = if ($PowerShellShell) { Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe' } else { $env:ComSpec }
    # command_runner.rs: command.arg("/C").raw_arg(format!("\"{command_line}\""))
    $command = $hook[0].command + ' -Port ' + $port
    if ($PowerShellShell) {
        $info.ArgumentList.Add('-NoProfile')
        $info.ArgumentList.Add('-Command')
        $info.ArgumentList.Add($command)
    } else {
        $info.Arguments = '/C "' + $command + '"'
    }
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($info)
    $process.StandardInput.WriteLine('{"tool_name":"exec_command","tool_input":{"cmd":"Get-Content README.md"}}')
    $process.StandardInput.Close()
    if (-not $process.WaitForExit(5000)) { $process.Kill(); throw 'Hook timed out.' }
    $stderr = $process.StandardError.ReadToEnd()
    $stdout = $process.StandardOutput.ReadToEnd()
    if ($process.ExitCode -ne 0 -or $stderr -or $stdout) {
        throw "Hook failed: exit=$($process.ExitCode), stderr=$stderr, stdout=$stdout"
    }
    if (-not $listener.Pending()) { throw 'Hook exited without delivering an event.' }
    $client = $listener.AcceptTcpClient()
    try {
        $client.ReceiveTimeout = 1000
        $reader = [IO.StreamReader]::new($client.GetStream())
        try { $line = $reader.ReadLine() } finally { $reader.Dispose() }
        if ($line -ne 'Codex:PreToolUse Lendo README.md...') { throw "Unexpected event: $line" }
    } finally { $client.Dispose() }
    Write-Output 'PASS: Codex Windows wrapper, silent exit, event delivery and text.'
} finally {
    $listener.Stop()
    if ($process) { $process.Dispose() }
}
