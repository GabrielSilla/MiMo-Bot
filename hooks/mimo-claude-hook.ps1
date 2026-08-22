<#
.SYNOPSIS
    Bridges a Claude Code hook event to Brobot.Sender's AiThoughtsListener
    (see src/Brobot.Sender/AiThoughtsListener.cs), which turns it into
    FACE/MSG commands toward Brobot Core.

.DESCRIPTION
    Invoked as a Claude Code "command" hook. Reads the event's JSON payload
    off stdin (if any), pulls out a short human-readable label for the events
    that need one, and sends a single line "EVENTNAME [text]" to
    127.0.0.1:<Port> — the same one-line-per-event wire shape PROTOCOL.md's
    own FACE/MSG lines use, just inbound instead of outbound.

    Must never fail the hook or block Claude Code: if MiMo isn't running, the
    "Atividade da IA" checkbox isn't on, or anything else goes wrong, this
    silently no-ops and always exits 0. UserPromptSubmit hooks specifically
    have their stdout appended to Claude's context, so this script is careful
    to never write anything to stdout.
#>
param(
    [Parameter(Mandatory = $true)][string]$EventName,
    [int]$Port = 5591
)

$ErrorActionPreference = "SilentlyContinue"

# Friendly Portuguese label per Claude Code tool name, for PreToolUse — kept
# here (not in Brobot.Sender) since it's Claude-Code-specific; a Codex/Gemini
# hook script would need its own mapping for its own tool names.
$ToolLabels = @{
    "Bash"       = "Executando comando..."
    "Read"       = "Lendo arquivo..."
    "Edit"       = "Editando arquivo..."
    "Write"      = "Escrevendo arquivo..."
    "Grep"       = "Procurando..."
    "Glob"       = "Procurando arquivos..."
    "WebFetch"   = "Pesquisando..."
    "WebSearch"  = "Pesquisando..."
    "Task"       = "Delegando tarefa..."
    "TodoWrite"  = "Organizando tarefas..."
}

function Read-StdinJson {
    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) {
        return $null
    }
    try {
        return $raw | ConvertFrom-Json
    } catch {
        return $null
    }
}

# MSG word-wraps into a fixed 3-line window on Core (see Face.cpp) — a very
# long description would just scroll, not break anything, but there's no
# reason to send more than fits comfortably.
function Limit-Length([string]$s, [int]$max = 100) {
    if ($s.Length -le $max) { return $s }
    return $s.Substring(0, $max - 1) + "…"
}

# Claude Code already generates a short present-tense description for some
# tool calls (Bash's tool_input.description, e.g. "Inspecting CTePack.Web
# folder contents and size") — that's far more useful than a generic
# per-tool label, so prefer it whenever it's there. Falls back to a
# tool-specific summary (file name, search pattern, ...) built from
# tool_input, then to the generic $ToolLabels entry, then to the raw tool
# name itself.
function Get-PreToolUseText($payload) {
    $toolName = $payload.tool_name
    $toolInput = $payload.tool_input

    if ($toolInput -and $toolInput.description) {
        return [string]$toolInput.description
    }

    if ($toolInput) {
        switch ($toolName) {
            "Read"  { if ($toolInput.file_path) { return "Lendo $(Split-Path $toolInput.file_path -Leaf)..." } }
            "Edit"  { if ($toolInput.file_path) { return "Editando $(Split-Path $toolInput.file_path -Leaf)..." } }
            "Write" { if ($toolInput.file_path) { return "Escrevendo $(Split-Path $toolInput.file_path -Leaf)..." } }
            "Grep"  { if ($toolInput.pattern) { return "Procurando '$($toolInput.pattern)'..." } }
            "Glob"  { if ($toolInput.pattern) { return "Procurando '$($toolInput.pattern)'..." } }
            "WebFetch"  { if ($toolInput.url) { return "Pesquisando $($toolInput.url)..." } }
            "WebSearch" { if ($toolInput.query) { return "Pesquisando '$($toolInput.query)'..." } }
            { $_ -in @("Bash", "PowerShell") } {
                # No description this time (e.g. left blank on the call) — the
                # command itself still beats showing just the tool's name.
                if ($toolInput.command) { return [string]$toolInput.command }
            }
        }
    }

    if ($toolName) {
        $label = $ToolLabels[$toolName]
        if ($label) { return $label }
        return $toolName
    }

    return $null
}

$text = $null
$payload = Read-StdinJson

if ($payload) {
    switch ($EventName) {
        "PreToolUse" {
            $text = Get-PreToolUseText $payload
        }
        "Notification" {
            if ($payload.message) { $text = [string]$payload.message }
        }
    }
}

if ($text) { $text = Limit-Length $text }

$line = if ($text) { "$EventName $text" } else { $EventName }

try {
    $client = New-Object System.Net.Sockets.TcpClient
    # ConnectAsync + Wait (not a plain blocking Connect) bounds how long a
    # hung/firewalled connection attempt can hold up Claude Code — a refused
    # connection (nothing listening) returns almost instantly either way.
    $connectTask = $client.ConnectAsync("127.0.0.1", $Port)
    if ($connectTask.Wait(300) -and $client.Connected) {
        $writer = New-Object System.IO.StreamWriter($client.GetStream())
        $writer.WriteLine($line)
        $writer.Flush()
    }
    $client.Close()
} catch {
    # MiMo not running, "Atividade da IA" unchecked, or any other failure —
    # this must never surface as a hook error or block Claude Code.
}

exit 0
