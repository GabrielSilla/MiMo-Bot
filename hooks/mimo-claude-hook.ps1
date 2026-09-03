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

.NOTES
    This file (and mimo-claude-statusline.ps1) carry a UTF-8 BOM on purpose —
    do not strip it. ClaudeCodeHookInstaller invokes these via classic
    `powershell.exe -File`, not `pwsh`, and Windows PowerShell 5.1 reads a
    BOM-less script file using the system's ANSI codepage rather than UTF-8.
    A literal accented character in a string here (e.g. "olha lá") got
    silently double-encoded into mojibake ("olha lÃ¡" on the wire) the moment
    the BOM was missing — a real bug, fixed once by adding the BOM rather
    than by avoiding accents in future strings.

    One script, every event: which events are actually registered is
    ClaudeCodeHookInstaller's call, not this file's — an event that is never
    registered simply never reaches the switch below. That's deliberate, so
    adding one is a single entry there plus a single arm here.

    Deliberately NOT registered, even though Claude Code offers them:
    PostToolUse (success), which carries nothing PreToolUse hasn't already
    said and would double the PowerShell processes spawned per tool call;
    and MessageDisplay / FileChanged / InstructionsLoaded / ConfigChange,
    which fire often enough to turn MiMo's screen into a strobe rather than
    a status display.
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

# The wire format is one line per event, so anything reaching MiMo has to
# survive being flattened onto a single line first. That's academic for a
# tool description, but Stop's last_assistant_message is real prose —
# multi-line, markdown, sometimes with fenced code in it — and one raw
# newline there would split a single event into two garbage lines on the
# listener's side.
function ConvertTo-SingleLine([string]$s) {
    if ([string]::IsNullOrWhiteSpace($s)) { return $null }
    $s = $s -replace '```[\s\S]*?```', ' '   # fenced code: never readable at 160x128 anyway
    $s = $s -replace '[\r\n\t]+', ' '
    $s = $s -replace '[*_`#>\[\]]', ''       # light markdown strip; Font5x7 has no styling to show
    $s = $s -replace '\s{2,}', ' '
    return $s.Trim()
}

# MiMo gets the first *sentence* of a long answer, not its first 100
# characters: a hard cut lands mid-word and reads as truncation, while one
# complete sentence reads as him actually saying something. The {10,} guard
# keeps an abbreviation or a version number in the opening words from being
# mistaken for the end of the sentence.
function Get-FirstSentence([string]$s) {
    $clean = ConvertTo-SingleLine $s
    if (-not $clean) { return $null }
    $match = [regex]::Match($clean, '^(.{10,}?[.!?])(\s|$)')
    if ($match.Success) { return $match.Groups[1].Value }
    return $clean
}

# model / from_model / to_model are documented only as "the model" — depending
# on the event they arrive either as a bare id string or as the same
# {id, display_name} object the status line gets, so both shapes are read
# rather than betting on one.
function Get-ModelName($value) {
    if (-not $value) { return $null }
    if ($value -is [string]) { return $value }
    if ($value.display_name) { return [string]$value.display_name }
    if ($value.id) { return [string]$value.id }
    return $null
}

# The folder name is only worth saying when it names a *project*. A session
# started from the home directory (which is where switching accounts lands
# you) produced "Bora trabalhar em Gabriel!", and a drive root would produce
# "Bora trabalhar em C:!" — both read as MiMo having misunderstood something.
# Returning null lets the caller fall back to a greeting with no place in it.
function Get-FolderName([string]$path) {
    if ([string]::IsNullOrWhiteSpace($path)) { return $null }

    $full = $path
    try { $full = [System.IO.Path]::GetFullPath($path) } catch { }

    # Trimmed only for *comparisons*. Never hand a trimmed "C:" to Split-Path:
    # it reads that as a drive-*relative* path and resolves it against the
    # current directory, so the drive root came back as whatever folder the
    # hook process happened to be sitting in — a greeting naming a project
    # nobody had opened.
    $normalized = $full.TrimEnd('\')

    $root = $null
    try { $root = [System.IO.Path]::GetPathRoot($full) } catch { }
    if ($root -and $normalized -ieq $root.TrimEnd('\')) { return $null }

    # Not $home: that's one of PowerShell's automatic variables, and shadowing
    # it here would be confusing at best.
    $profileDir = $null
    try { $profileDir = [System.IO.Path]::GetFullPath($env:USERPROFILE).TrimEnd('\') } catch { }
    if ($profileDir -and $normalized -ieq $profileDir) { return $null }

    $leaf = Split-Path $full -Leaf
    if ([string]::IsNullOrWhiteSpace($leaf)) { return $null }
    return $leaf
}

# MSG word-wraps into a fixed 3-line window on Core (see Face.cpp) — a very
# long description would just scroll, not break anything, but there's no
# reason to send more than fits comfortably.
function Limit-Length([string]$s, [int]$max = 100) {
    if ([string]::IsNullOrEmpty($s)) { return $s }
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

# A failed tool says more with its own error text than with its name, but
# tool_response's shape is per-tool (a plain string for some, an object with
# stderr/error for others), so this reads whichever is actually there and
# gives up gracefully rather than betting on a schema.
function Get-ToolFailureText($payload) {
    $toolName = if ($payload.tool_name) { [string]$payload.tool_name } else { "A ferramenta" }
    $response = $payload.tool_response

    $detail = $null
    if ($response -is [string]) {
        $detail = $response
    } elseif ($response) {
        foreach ($field in @("error", "stderr", "message")) {
            if ($response.$field -is [string] -and -not [string]::IsNullOrWhiteSpace($response.$field)) {
                $detail = [string]$response.$field
                break
            }
        }
    }

    $detail = ConvertTo-SingleLine $detail
    if ($detail) { return "$toolName falhou: $detail" }
    return "$toolName falhou"
}

$text = $null
$payload = Read-StdinJson

if ($payload) {
    switch ($EventName) {
        "SessionStart" {
            $model = Get-ModelName $payload.model
            $folder = Get-FolderName ([string]$payload.cwd)
            if ($model -and $folder) { $text = "$model na area! Projeto: $folder" }
            elseif ($folder)         { $text = "Bora trabalhar em $folder!" }
            elseif ($model)          { $text = "$model na area!" }
        }

        "PreToolUse" {
            $text = Get-PreToolUseText $payload
            # agent_type is present only while the call comes from a subagent,
            # so this is the one place MiMo can tell "Claude is doing this"
            # apart from "something Claude delegated is doing this".
            if ($text -and $payload.agent_type) {
                $text = "($($payload.agent_type)) $text"
            }
        }

        "PostToolUseFailure" { $text = Get-ToolFailureText $payload }

        "PermissionRequest" {
            # $($toolName), not $toolName: "?" is a legal character in a
            # PowerShell variable name (hence the automatic $?), so
            # "$toolName?" interpolates a variable called "toolName?" — which
            # doesn't exist — and silently swallows both the name and the
            # question mark.
            $text = if ($payload.tool_name) { "Posso usar o $($payload.tool_name)?" } else { "Posso fazer isso?" }
        }

        "PermissionDenied" {
            $toolName = if ($payload.tool_name) { [string]$payload.tool_name } else { "A ferramenta" }
            $text = "$toolName foi bloqueado."
        }

        "Notification" {
            if ($payload.message) { $text = [string]$payload.message }
        }

        "SubagentStart" {
            $agent = if ($payload.agent_type) { [string]$payload.agent_type } else { "um subagente" }
            $text = "Chamei $agent pra ajudar..."
        }

        "SubagentStop" {
            # Deliberately a fixed phrase, not last_assistant_message: a
            # subagent's own final reply reads as a stray, out-of-context line
            # on a screen this small ("commita isso aí" with nothing before
            # it) rather than as a status update — unlike Stop's own
            # last_assistant_message, which is the main agent reporting to
            # the person actually watching the screen.
            $text = "O subagente do Claude finalizou, olha lá!"
        }

        # The whole reason Stop is worth a payload read at all:
        # last_assistant_message is the actual text Claude just finished
        # saying, so MiMo reports what he did instead of a fixed "Terminei!".
        "Stop" { $text = Get-FirstSentence ([string]$payload.last_assistant_message) }

        "PreModelSwitch" {
            $to = Get-ModelName $payload.to_model
            if ($to) { $text = "Trocando pro $to..." }
        }

        "PostModelSwitch" {
            $to = Get-ModelName $payload.to_model
            if ($to) { $text = "Agora eu sou o $to." }
        }

        "CwdChanged" {
            $folder = Get-FolderName ([string]$payload.cwd)
            if ($folder) { $text = "Mudei pra $folder." }
        }
    }
}

if ($text) { $text = Limit-Length (ConvertTo-SingleLine $text) }

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
