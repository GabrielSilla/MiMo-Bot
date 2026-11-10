<#
.SYNOPSIS
    Bridges an OpenAI Codex hook event to Brobot.Sender's AiThoughtsListener
    (see src/Brobot.Sender/AiThoughtsListener.cs), which turns it into
    FACE/MSG commands toward Brobot Core.

.DESCRIPTION
    Codex's counterpart of peemo-claude-hook.ps1. Invoked as a Codex
    "command" hook registered in ~/.codex/hooks.json by CodexHookInstaller.
    Reads the event's JSON payload off stdin, pulls out a short
    human-readable label, and sends a single line "Codex:EVENTNAME [text]"
    to 127.0.0.1:<Port>. The "Codex:" is what makes the Sender put
    "Codex: " in front of the message on Peemo's screen; the prefix itself,
    and fitting it to the screen, is the Sender's job (AiMessageFit), never
    this script's and never Core's.

    Must never fail the hook or slow Codex down: every failure path is
    swallowed and it always exits 0 with *no output at all*. That's stricter
    here than for Claude: Codex adds plain stdout to the model's context for
    SessionStart/UserPromptSubmit/SubagentStart, and treats any non-JSON
    stdout on Stop/SubagentStop/Interrupt as an invalid hook result.

.NOTES
    UTF-8 BOM on purpose, same reason as peemo-claude-hook.ps1: it runs under
    Windows PowerShell 5.1 (`powershell -File`), which reads a BOM-less
    script in the ANSI codepage and would mangle the accents below.

    Codex's events are not Claude Code's. What exists (developers.openai.com
    /codex/hooks, checked against Codex CLI 0.167): SessionStart,
    UserPromptSubmit, PreToolUse, PermissionRequest, PostToolUse,
    PreCompact, PostCompact, SubagentStart, SubagentStop, Stop, Interrupt,
    SessionEnd, Pre/PostModelRequest. There is no Notification, no
    PostToolUseFailure, no StopFailure, no PermissionDenied, no model-switch
    or task events. Which ones are registered is CodexHookInstaller's call;
    an event that isn't registered just never reaches the switch below.

    Helpers are copied from peemo-claude-hook.ps1 rather than shared, so each
    script stands on its own next to the exe and a change for one tool can't
    quietly break the other.
#>
param(
    [Parameter(Mandatory = $true)][string]$EventName,
    [int]$Port = 5591
)

$ErrorActionPreference = "SilentlyContinue"

# Diagnostics contain lifecycle stages only, never prompts or tool payloads.
# Keep stdout empty because it is part of Codex's hook response protocol.
function Write-HookDiagnostic([string]$stage) {
    try {
        $logPath = Join-Path $PSScriptRoot 'peemo-codex-hook.log'
        if ((Test-Path $logPath) -and (Get-Item $logPath).Length -gt 131072) {
            [IO.File]::WriteAllText($logPath, '')
        }
        [IO.File]::AppendAllText($logPath, "$(Get-Date -Format o) pid=$PID event=$EventName $stage`r`n")
    } catch { }
}
Write-HookDiagnostic 'started'

# Codex writes the payload as UTF-8; Windows PowerShell would otherwise
# decode stdin with the OEM codepage and turn accents into mojibake (same
# bug, same fix as peemo-claude-hook.ps1).
[Console]::InputEncoding = New-Object System.Text.UTF8Encoding($false)

function Read-StdinJson {
    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
    try { return $raw | ConvertFrom-Json } catch { return $null }
}

function ConvertTo-SingleLine([string]$s) {
    if ([string]::IsNullOrWhiteSpace($s)) { return $null }
    $s = $s -replace '```[\s\S]*?```', ' '
    $s = $s -replace '[\r\n\t]+', ' '
    $s = $s -replace '[*_`#>\[\]]', ''
    $s = $s -replace '\s{2,}', ' '
    return $s.Trim()
}

function Get-FirstSentence([string]$s) {
    $clean = ConvertTo-SingleLine $s
    if (-not $clean) { return $null }
    $match = [regex]::Match($clean, '^(.{10,}?[.!?])(\s|$)')
    if ($match.Success) { return $match.Groups[1].Value }
    return $clean
}

# Same rules as peemo-claude-hook.ps1's: no project name for the home
# directory or a drive root, and never hand a trimmed "C:" to Split-Path.
function Get-FolderName([string]$path) {
    if ([string]::IsNullOrWhiteSpace($path)) { return $null }
    $full = $path
    try { $full = [System.IO.Path]::GetFullPath($path) } catch { }
    $normalized = $full.TrimEnd('\')
    $root = $null
    try { $root = [System.IO.Path]::GetPathRoot($full) } catch { }
    if ($root -and $normalized -ieq $root.TrimEnd('\')) { return $null }
    $profileDir = $null
    try { $profileDir = [System.IO.Path]::GetFullPath($env:USERPROFILE).TrimEnd('\') } catch { }
    if ($profileDir -and $normalized -ieq $profileDir) { return $null }
    $leaf = Split-Path $full -Leaf
    if ([string]::IsNullOrWhiteSpace($leaf)) { return $null }
    return $leaf
}

# Wire-level backstop only. The real fit — prefix included, against Core's
# 3-line wrap — happens in the Sender (AiMessageFit), which knows the
# prefix; this just keeps a runaway command line from being sent whole.
function Limit-Length([string]$s, [int]$max = 100) {
    if ([string]::IsNullOrEmpty($s)) { return $s }
    if ($s.Length -le $max) { return $s }
    return $s.Substring(0, $max - 3) + "..."   # three periods, never "…" (no glyph on Core)
}

function Get-Leaf([string]$p) {
    if ([string]::IsNullOrWhiteSpace($p)) { return $null }
    $p = $p.Trim().Trim('"', "'")
    $leaf = $null
    try { $leaf = Split-Path $p -Leaf } catch { }
    if ([string]::IsNullOrWhiteSpace($leaf)) { return $p }
    return $leaf
}

# Codex's shell tool hands over either a string or an argv array, and on
# Windows the string is often wrapped ("powershell.exe -Command '...'",
# "bash -lc '...'"). Peel that off so what's left is what the model asked
# to run.
function Get-ShellCommandText($commandValue) {
    if ($null -eq $commandValue) { return $null }
    $cmd = $null
    if ($commandValue -is [string]) {
        $cmd = $commandValue
    } elseif ($commandValue -is [System.Array]) {
        $parts = @($commandValue | ForEach-Object { [string]$_ })
        if ($parts.Count -eq 0) { return $null }
        # argv form: [shell, flag(s)..., script] — the script is the last item
        # whenever the first one is a shell.
        if ($parts.Count -ge 2 -and $parts[0] -match '(?i)(^|[\\/])(bash|sh|zsh|powershell|pwsh|cmd)(\.exe)?$') {
            $cmd = $parts[$parts.Count - 1]
        } else {
            $cmd = $parts -join ' '
        }
    } else {
        $cmd = [string]$commandValue
    }

    $cmd = $cmd.Trim()
    $wrapper = [regex]::Match($cmd, '(?is)^\s*"?(?:[^"\s]*[\\/])?(?:powershell|pwsh)(?:\.exe)?"?\s+(?:-\w+\s+)*?-(?:Command|c)\s+(.+)$')
    if (-not $wrapper.Success) {
        $wrapper = [regex]::Match($cmd, '(?is)^\s*"?(?:[^"\s]*[\\/])?(?:bash|sh|zsh)(?:\.exe)?"?\s+-l?c\s+(.+)$')
    }
    if (-not $wrapper.Success) {
        $wrapper = [regex]::Match($cmd, '(?is)^\s*cmd(?:\.exe)?\s+/[cC]\s+(.+)$')
    }
    if ($wrapper.Success) {
        $cmd = $wrapper.Groups[1].Value.Trim()
        if ($cmd.Length -ge 2 -and (($cmd[0] -eq '"' -and $cmd[-1] -eq '"') -or ($cmd[0] -eq "'" -and $cmd[-1] -eq "'"))) {
            $cmd = $cmd.Substring(1, $cmd.Length - 2)
        }
    }
    return $cmd.Trim()
}

# A friendlier line for the commands Codex runs most to look around — what
# a person watching would call "reading X" or "searching for Y" — falling
# back to the command itself, which still beats a bare tool name.
function Split-CommandTokens([string]$s) {
    return @([regex]::Matches($s, '"[^"]*"|''[^'']*''|\S+') | ForEach-Object { $_.Value })
}

# The first argument that isn't a flag, skipping the value of each flag in
# $valueFlags ("-TotalCount 20", "-g '*.cs'"). A flag *not* listed is taken
# as a switch, so its next token counts — which is exactly right for
# "-Path <file>" and "rg -e <pattern>", whose value is the operand wanted.
function Get-FirstOperand([string[]]$tokens, [string[]]$valueFlags) {
    for ($i = 1; $i -lt $tokens.Count; $i++) {
        $t = $tokens[$i]
        if ($t -match '^--?([\w-]+)(=.*)?$') {
            if (-not $Matches[2] -and ($valueFlags -contains $Matches[1].ToLowerInvariant())) { $i++ }
            continue
        }
        return $t.Trim('"', "'")
    }
    return $null
}

function Get-ShellSummary([string]$cmd) {
    if ([string]::IsNullOrWhiteSpace($cmd)) { return $null }
    # Only the first command of a chain: "Get-Content a; rg b" is "reading a".
    $first = ($cmd -split '\s*(?:;|&&|\|\||\|)\s*', 2)[0].Trim()
    $tokens = Split-CommandTokens $first
    if ($tokens.Count -eq 0) { return $cmd }
    $verb = $tokens[0].ToLowerInvariant()

    if ($verb -in @("get-content", "gc", "cat", "type", "head", "tail", "more", "less")) {
        $file = Get-FirstOperand $tokens @("totalcount", "tail", "head", "first", "encoding", "delimiter", "readcount", "n", "c")
        if ($file) { return "Lendo $(Get-Leaf $file)..." }
    }
    if ($verb -eq "sed" -and $tokens.Count -ge 3) {
        return "Lendo $(Get-Leaf $tokens[$tokens.Count - 1])..."
    }

    if ($verb -in @("rg", "grep", "findstr", "select-string", "sls")) {
        if ($tokens -contains "--files") { return "Procurando arquivos..." }
        # No "c": rg's -C takes a value but grep's -c is a switch, and these
        # are compared lowercased.
        $pattern = Get-FirstOperand $tokens @("g", "glob", "t", "type", "type-not", "context", "a", "after-context",
            "b", "before-context", "m", "max-count", "max-columns", "j", "threads", "encoding", "path", "literalpath", "include", "exclude")
        if ($pattern) { return "Procurando '$pattern'..." }
    }

    if ($verb -in @("get-childitem", "gci", "ls", "dir", "tree", "find")) { return "Olhando os arquivos..." }

    return $cmd
}

# apply_patch's input is the patch text itself; its "*** Add/Update/Delete
# File: <path>" headers say exactly which files are being touched.
function Get-PatchSummary([string]$patch) {
    if ([string]::IsNullOrWhiteSpace($patch)) { return $null }
    $files = [regex]::Matches($patch, '(?m)^\*\*\* (Add|Update|Delete) File:\s*(.+?)\s*$')
    if ($files.Count -eq 0) { return "Editando arquivo..." }
    $verb = switch ($files[0].Groups[1].Value) {
        "Add"    { "Criando" }
        "Delete" { "Apagando" }
        default  { "Editando" }
    }
    $leaf = Get-Leaf $files[0].Groups[2].Value
    if ($files.Count -gt 1) { return "$verb $leaf e mais $($files.Count - 1)..." }
    return "$verb $leaf..."
}

function Get-PreToolUseText($payload) {
    $toolName = [string]$payload.tool_name
    $toolInput = $payload.tool_input

    # PermissionRequest documents tool_input.description; prefer it wherever
    # Codex provides one, same as the Claude script does.
    if ($toolInput -and -not ($toolInput -is [string]) -and $toolInput.description) {
        return [string]$toolInput.description
    }

    # Code-mode turns (the desktop app's "exec" tool) carry a JS snippet as a
    # plain string: dig the shell command out of its exec_command({cmd: ...}).
    if ($toolInput -is [string]) {
        $m = [regex]::Match($toolInput, 'exec_command\(\{\s*"?cmd"?\s*:\s*"((?:[^"\\]|\\.)*)"')
        if ($m.Success) {
            $raw = $m.Groups[1].Value
            try { $raw = ConvertFrom-Json ('"' + $raw + '"') } catch { }
            return Get-ShellSummary (Get-ShellCommandText $raw)
        }
        if ($toolInput -match '(?m)^\*\*\* (Add|Update|Delete) File:') { return Get-PatchSummary $toolInput }
    }

    if ($toolName -match '^(?i)(apply_patch|Edit|Write)$') {
        $patch = $null
        if ($toolInput -is [string]) { $patch = $toolInput }
        elseif ($toolInput) {
            foreach ($field in @("command", "input", "patch")) {
                if ($toolInput.$field -is [string]) { $patch = [string]$toolInput.$field; break }
            }
        }
        return Get-PatchSummary $patch
    }

    if ($toolInput -and -not ($toolInput -is [string])) {
        foreach ($field in @("command", "cmd")) {
            if ($null -ne $toolInput.$field) {
                $summary = Get-ShellSummary (Get-ShellCommandText $toolInput.$field)
                if ($summary) { return $summary }
            }
        }
    }

    # MCP tools arrive as mcp__<server>__<tool>.
    $mcp = [regex]::Match($toolName, '^mcp__(.+?)__(.+)$')
    # Underscores become spaces here because ConvertTo-SingleLine strips "_"
    # as markdown later, which would glue "create_issue" into "createissue".
    if ($mcp.Success) { return "Usando $($mcp.Groups[2].Value -replace '_', ' ') ($($mcp.Groups[1].Value -replace '_', ' '))..." }

    switch -Regex ($toolName) {
        '^(?i)(Bash|shell|local_shell|exec_command|exec)$' { return "Executando comando..." }
        '^(?i)update_plan$' { return "Organizando tarefas..." }
        '^(?i)view_image$'  { return "Olhando uma imagem..." }
    }

    if ($toolName) { return $toolName }
    return $null
}

$text = $null
$skip = $false
$payload = Read-StdinJson
Write-HookDiagnostic 'stdin-read'

if ($payload) {
    switch ($EventName) {
        "SessionStart" {
            $model = [string]$payload.model
            $folder = Get-FolderName ([string]$payload.cwd)
            switch ([string]$payload.source) {
                # Codex fires SessionStart again after compacting a session;
                # greeting (and a HAPPY face) mid-work would be wrong.
                # PostCompact already reports the compaction.
                "compact" { $skip = $true }
                "resume" {
                    if ($folder) { $text = "De volta em $folder!" } else { $text = "De volta!" }
                }
                default {
                    if ($model -and $folder) { $text = "$model na area! Projeto: $folder" }
                    elseif ($folder)         { $text = "Bora trabalhar em $folder!" }
                    elseif ($model)          { $text = "$model na area!" }
                }
            }
        }

        "PreToolUse" {
            $text = Get-PreToolUseText $payload
            if ($text -and $payload.agent_type) {
                $text = "($($payload.agent_type)) $text"
            }
        }

        "PermissionRequest" {
            # $($payload.tool_name), not "$toolName?": "?" is legal in a
            # PowerShell variable name and would swallow both.
            $text = if ($payload.tool_name) { "Posso usar o $($payload.tool_name)?" } else { "Posso fazer isso?" }
        }

        "SubagentStart" {
            $agent = if ($payload.agent_type) { [string]$payload.agent_type } else { "um subagente" }
            $text = "Chamei $agent pra ajudar..."
        }

        # Fixed phrase, not last_assistant_message — same reasoning as the
        # Claude script: a subagent's own reply is an out-of-context line on
        # a screen this small.
        "SubagentStop" { $text = "O subagente finalizou, olha lá!" }

        # Sent for the event log; the Sender always shows "Terminei!" for
        # Stop regardless (see MainWindow.OnAiThoughtReceived).
        "Stop" { $text = Get-FirstSentence ([string]$payload.last_assistant_message) }
    }
}

if ($skip) { exit 0 }

if ($text) { $text = Limit-Length (ConvertTo-SingleLine $text) }

$line = if ($text) { "Codex:$EventName $text" } else { "Codex:$EventName" }

try {
    $client = New-Object System.Net.Sockets.TcpClient
    # Bounded connect: Codex waits for this process (hooks run synchronously,
    # which is also what keeps their order), so a hung attempt must not
    # stall it. A refused connection returns almost instantly anyway.
    $connectTask = $client.ConnectAsync("127.0.0.1", $Port)
    if ($connectTask.Wait(300) -and $client.Connected) {
        $writer = New-Object System.IO.StreamWriter($client.GetStream(), (New-Object System.Text.UTF8Encoding($false)))
        $writer.WriteLine($line)
        $writer.Flush()
        Write-HookDiagnostic 'sent'
    } else {
        Write-HookDiagnostic 'connect-timeout'
    }
    $client.Close()
} catch {
    Write-HookDiagnostic ("failed: " + $_.Exception.Message)
    # Peemo not running, or anything else — never a hook error.
}

exit 0
