<#
.SYNOPSIS
    Bridges Claude Code's statusLine feature to Brobot.Sender's
    AiThoughtsListener, so MiMo can show token/context usage per turn.

.DESCRIPTION
    Invoked by Claude Code as the "statusLine" command (see
    ClaudeCodeHookInstaller.cs) — a different contract from the hooks in
    mimo-claude-hook.ps1: it receives a much richer JSON payload (model,
    cost, context_window, ...) on stdin, and unlike a hook, THIS script's
    own stdout is what Claude Code actually renders as the terminal's
    status line, not just logged. So it both prints a short status for the
    terminal and, as a side effect, forwards the same numbers to MiMo over
    TCP using the same one-line-per-event wire shape AiThoughtsListener
    already understands.

    Must never leave the terminal's status line blank: any failure below
    (bad/missing JSON, MiMo not running, ...) still falls through to
    printing something reasonable.

.NOTES
    context_window.total_output_tokens is *not* a running session total
    despite the name — per Claude Code's own docs it's just the most recent
    response's output token count. The only field that behaves like a
    running total is total_input_tokens (tokens currently loaded in the
    context window, which grows over the session and resets after a
    /compact) — that's what "percentual do contexto" is computed from here,
    per the user's own request to treat it as the session total.
#>
param(
    [int]$Port = 5591
)

$ErrorActionPreference = "SilentlyContinue"

$raw = [Console]::In.ReadToEnd()
$payload = $null
if (-not [string]::IsNullOrWhiteSpace($raw)) {
    try { $payload = $raw | ConvertFrom-Json } catch { $payload = $null }
}

$lastRequestTokens = 0
$totalTokens = 0
$windowSize = 0
$pct = 0

if ($payload -and $payload.context_window) {
    $cw = $payload.context_window

    if ($cw.current_usage) {
        $u = $cw.current_usage
        $lastRequestTokens = [int]($u.input_tokens) + [int]($u.output_tokens) `
            + [int]($u.cache_creation_input_tokens) + [int]($u.cache_read_input_tokens)
    }

    if ($cw.total_input_tokens) { $totalTokens = [int]$cw.total_input_tokens }
    if ($cw.context_window_size) { $windowSize = [int]$cw.context_window_size }
    if ($windowSize -gt 0) {
        $pct = [math]::Round(($totalTokens / $windowSize) * 100, 1)
    }
}

$statusText = "$lastRequestTokens tokens gastos na requisicao. $pct% do contexto utilizado"

# This line IS Claude Code's own terminal status line — silence here isn't
# optional the way it is in mimo-claude-hook.ps1.
Write-Output $statusText

try {
    $client = New-Object System.Net.Sockets.TcpClient
    # ConnectAsync + Wait (not a plain blocking Connect), same reasoning as
    # mimo-claude-hook.ps1: bounds how long a hung/firewalled attempt can
    # hold up Claude Code's status line refresh.
    $connectTask = $client.ConnectAsync("127.0.0.1", $Port)
    if ($connectTask.Wait(300) -and $client.Connected) {
        $writer = New-Object System.IO.StreamWriter($client.GetStream())
        $writer.WriteLine("ContextUsage $statusText")
        $writer.Flush()
    }
    $client.Close()
} catch {
    # MiMo not running, "Atividade da IA" unchecked, or any other failure —
    # must never affect the terminal's own status line above.
}
