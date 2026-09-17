<#
.SYNOPSIS
    Bridges a git hook event to Brobot.Sender's AiThoughtsListener (see
    src/Brobot.Sender/AiThoughtsListener.cs), which turns it into FACE/MSG
    commands toward Brobot Core.

.DESCRIPTION
    Invoked by one of the static shim scripts under hooks/git-hooks/
    (post-commit, post-merge, post-checkout, pre-push) with -EventName set to
    the wire event name MainWindow.OnAiThoughtReceived expects. Pulls a short
    human-readable label out of the repo git itself is already sitting in
    (git hooks run with the working tree as CWD), then sends a single line
    "EVENTNAME [text]" to 127.0.0.1:<Port> — the exact same wire shape
    mimo-claude-hook.ps1 already uses for the Claude Code bridge.

    Must never fail the hook or block the git operation that invoked it: any
    failure (MiMo not running, "Ferramentas de Dev" unchecked, git itself
    missing) silently no-ops and this always exits 0. pre-push in particular
    is NOT advisory — a non-zero exit there aborts the push — so the calling
    shim also forces exit 0 regardless of what this script does.

.NOTES
    This file carries a UTF-8 BOM on purpose, same reasoning as
    mimo-claude-hook.ps1's own header comment: invoked via classic
    `powershell.exe -File`, not `pwsh`, and Windows PowerShell 5.1 reads a
    BOM-less script using the system ANSI codepage rather than UTF-8, which
    silently mangles any accented PT-BR text (commit messages, branch names)
    read from here.

    There is no "push succeeded" event: pre-push is the only push-related
    git hook, and it fires before the network round-trip, so GitPush can only
    ever mean "a push just started" — see MainWindow.OnAiThoughtReceived's
    own comment on why that maps to FACE BUILDING rather than FINISHED.
    post-commit/post-merge, by contrast, only fire once the operation already
    succeeded (git stops before invoking them on a failed/conflicted one), so
    those two need no separate "it worked" signal at all.
#>

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("GitCommit", "GitMerge", "GitCheckout", "GitPush")]
    [string]$EventName,

    [int]$Port = 5591
)

$ErrorActionPreference = "SilentlyContinue"

function ConvertTo-SingleLine([string]$s) {
    if ([string]::IsNullOrWhiteSpace($s)) { return $null }
    $s = $s -replace '[\r\n\t]+', ' '
    $s = $s -replace '\s{2,}', ' '
    return $s.Trim()
}

function Limit-Length([string]$s, [int]$max = 100) {
    if ([string]::IsNullOrEmpty($s)) { return $s }
    if ($s.Length -le $max) { return $s }
    # Three literal periods, not "…" — see mimo-claude-hook.ps1's own note:
    # Core reads the wire byte-at-a-time in single-byte Latin glyphs, so a
    # multi-byte UTF-8 ellipsis draws as invisible gaps instead of a visible
    # "this was cut" cue. A real bug there, avoided here from the start.
    return $s.Substring(0, $max - 3) + "..."
}

function Get-CurrentBranch {
    $branch = (git rev-parse --abbrev-ref HEAD 2>$null)
    if ($branch -and $branch -ne "HEAD") { return $branch.Trim() }
    return $null
}

$text = $null

switch ($EventName) {
    "GitCommit" {
        $subject = (git log -1 --pretty=%s 2>$null)
        $branch = Get-CurrentBranch
        if ($subject) {
            $text = if ($branch) { "Commit: $subject. Branch: $branch" } else { "Commit: $subject" }
        }
    }

    "GitMerge" {
        $branch = Get-CurrentBranch
        if ($branch) { $text = "Merge em $branch." }
    }

    "GitCheckout" {
        # The calling shim (hooks/git-hooks/post-checkout) already filters
        # out plain file checkouts via git's own third hook argument — every
        # invocation reaching this script is a real branch switch.
        $branch = Get-CurrentBranch
        if ($branch) { $text = "Foi pra branch $branch." }
    }

    "GitPush" {
        $branch = Get-CurrentBranch
        $text = if ($branch) { "push iniciado para branch: $branch" } else { "push iniciado" }
    }
}

if ($text) { $text = Limit-Length (ConvertTo-SingleLine $text) }

$line = if ($text) { "$EventName $text" } else { $EventName }

try {
    $client = New-Object System.Net.Sockets.TcpClient
    # ConnectAsync + Wait (not a plain blocking Connect) bounds how long a
    # hung/firewalled connection attempt can hold up the git operation that
    # invoked this hook — a refused connection (nothing listening) returns
    # almost instantly either way.
    $connectTask = $client.ConnectAsync("127.0.0.1", $Port)
    if ($connectTask.Wait(300) -and $client.Connected) {
        # BOM-less UTF-8 on the wire — same as mimo-claude-hook.ps1.
        $writer = New-Object System.IO.StreamWriter($client.GetStream(), (New-Object System.Text.UTF8Encoding($false)))
        $writer.WriteLine($line)
        $writer.Flush()
    }
    $client.Close()
} catch {
    # MiMo not running, "Ferramentas de Dev" unchecked, or any other failure
    # — must never surface as a hook error or block the git operation.
}

exit 0
