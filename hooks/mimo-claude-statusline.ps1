<#
.SYNOPSIS
    Bridges Claude Code's statusLine feature to Brobot.Sender's
    AiThoughtsListener, so MiMo can show token/context usage per turn.

.DESCRIPTION
    Invoked by Claude Code as the "statusLine" command (see
    ClaudeCodeHookInstaller.cs) — a different contract from the hooks in
    mimo-claude-hook.ps1: it receives a much richer JSON payload (model,
    cost, context_window, rate_limits, workspace, ...) on stdin, and unlike
    a hook, THIS script's own stdout is what Claude Code actually renders as
    the terminal's status line, not just logged. So it both prints a short
    status for the terminal and, as a side effect, forwards what it read to
    MiMo over TCP using the same one-line-per-event wire shape
    AiThoughtsListener already understands.

    Two lines are sent, not one, because they answer different questions and
    Core treats them differently:

      ContextUsage <text>   the same sentence printed in the terminal, shown
                            as an ordinary MSG — this is what CLASSIC and
                            MI2MO2 (which have no console log or stat panel)
                            get, and it is unchanged from before.
      AiStats <fields>      the numbers as numbers, which become PROTOCOL.md's
                            AISTATS and get drawn as a persistent panel in
                            the AI tab of the log themes (MATRIX, MI84).

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

    context_window.used_percentage would give the same figure pre-calculated,
    but the percentage is deliberately still computed here so the number in
    the terminal and the number on MiMo's screen can never disagree: they are
    the same variable.
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

# --- AISTATS ---------------------------------------------------------------
#
# Every field is an integer, and -1 means "this payload didn't carry it" —
# the same convention STATS already uses for a sensor the PC couldn't read,
# and for the same reason: Core draws "--" rather than inventing a zero. A
# session with no API call yet has a null current_usage, and rate_limits /
# cost are absent on some plans and setups, so none of these can be assumed.
function Get-IntOrUnknown($value) {
    if ($null -eq $value) { return -1 }
    return [int][math]::Round([double]$value)
}

$ctxPct = if ($windowSize -gt 0) { [int][math]::Round($pct) } else { -1 }

# Cost travels in cents, not dollars: the wire is integers only (see
# Personality::onAiStatsCommand, which parses with strtol), and cents is the
# smallest unit anyone reads off a screen this size.
$costCents = -1
if ($payload -and $payload.cost -and $null -ne $payload.cost.total_cost_usd) {
    $costCents = [int][math]::Round([double]$payload.cost.total_cost_usd * 100)
}

$rateFiveHour = -1
$rateSevenDay = -1
if ($payload -and $payload.rate_limits) {
    $rateFiveHour = Get-IntOrUnknown $payload.rate_limits.five_hour.used_percentage
    $rateSevenDay = Get-IntOrUnknown $payload.rate_limits.seven_day.used_percentage
}

# Trailing free text, so it may contain spaces ("Claude Opus 5") and may be
# empty — Core treats an empty tail as "no model name" and just leaves that
# column out.
$modelName = ""
if ($payload -and $payload.model -and $payload.model.display_name) {
    $modelName = ([string]$payload.model.display_name) -replace '[\r\n\t]+', ' '
}

$aiStatsLine = "AiStats $ctxPct $costCents $rateFiveHour $rateSevenDay $modelName".TrimEnd()

function Send-Line([string]$line) {
    try {
        $client = New-Object System.Net.Sockets.TcpClient
        # ConnectAsync + Wait (not a plain blocking Connect), same reasoning as
        # mimo-claude-hook.ps1: bounds how long a hung/firewalled attempt can
        # hold up Claude Code's status line refresh.
        $connectTask = $client.ConnectAsync("127.0.0.1", $Port)
        if ($connectTask.Wait(300) -and $client.Connected) {
            $writer = New-Object System.IO.StreamWriter($client.GetStream())
            $writer.WriteLine($line)
            $writer.Flush()
        }
        $client.Close()
    } catch {
        # MiMo not running, "Atividade da IA" unchecked, or any other failure —
        # must never affect the terminal's own status line above.
    }
}

# One connection per line, rather than both lines down one socket:
# AiThoughtsListener reads exactly one line per connection and closes (see
# HandleClient), which is what keeps a hook — a process that connects,
# writes and exits — from needing to signal anything.
Send-Line "ContextUsage $statusText"
Send-Line $aiStatsLine
