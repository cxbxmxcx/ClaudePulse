namespace ClaudePulse.Models;

/// <summary>
/// Immutable snapshot of one Claude Code session, merged from the
/// ~/.claude/sessions/&lt;pid&gt;.json registry file and transcript usage stats.
/// </summary>
public sealed class SessionInfo
{
    public int Pid { get; init; }
    public string SessionId { get; init; } = "";
    public string Cwd { get; init; } = "";
    public string Name { get; init; } = "";
    public string Entrypoint { get; init; } = "";
    public string Status { get; init; } = "";
    public string Version { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    // From the working directory's git repo
    public string? GitBranch { get; set; }
    public string? GitCommit { get; set; }

    // From transcript (JSONL) tailing
    public long ContextTokens { get; set; }
    public long OutputTokens { get; set; }
    public string? Model { get; set; }
    public string? Slug { get; set; }
    public DateTimeOffset? LastActivity { get; set; }

    /// <summary>
    /// CLI sessions report status directly. Desktop-app sessions never set
    /// status or updatedAt, so infer from transcript activity: recent writes
    /// mean it's working, silence for 30+ minutes means dormant.
    /// </summary>
    public string EffectiveStatus
    {
        get
        {
            if (!string.IsNullOrEmpty(Status)) return Status;
            DateTimeOffset? last = LastActivity;
            if (last is null && UpdatedAt > DateTimeOffset.MinValue) last = UpdatedAt;
            if (last is null) return "dormant";
            var age = DateTimeOffset.Now - last.Value;
            if (age < TimeSpan.FromMinutes(3)) return "busy";
            if (age < TimeSpan.FromMinutes(30)) return "idle";
            return "dormant";
        }
    }

    public bool IsBusy => string.Equals(EffectiveStatus, "busy", StringComparison.OrdinalIgnoreCase);
    public bool IsDormant => EffectiveStatus == "dormant";
    public string FolderLeaf => string.IsNullOrEmpty(Cwd) ? "" : System.IO.Path.GetFileName(Cwd.TrimEnd('\\', '/'));
}
