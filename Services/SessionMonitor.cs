using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudePulse.Models;

namespace ClaudePulse.Services;

/// <summary>
/// Reads the live session registry (~/.claude/sessions/*.json), filters out
/// dead processes, and enriches each session with token usage tailed from its
/// transcript under ~/.claude/projects/.
/// </summary>
public sealed class SessionMonitor
{
    private readonly string _sessionsDir;
    private readonly string _projectsDir;
    private readonly Dictionary<string, UsageState> _usage = new();

    public SessionMonitor()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _sessionsDir = Path.Combine(home, ".claude", "sessions");
        _projectsDir = Path.Combine(home, ".claude", "projects");
    }

    public List<SessionInfo> Snapshot()
    {
        var result = new List<SessionInfo>();
        if (!Directory.Exists(_sessionsDir)) return result;

        foreach (var file in Directory.EnumerateFiles(_sessionsDir, "*.json"))
        {
            SessionInfo? info = TryParseRegistryFile(file);
            if (info is null || !IsProcessAlive(info)) continue;
            EnrichWithUsage(info);
            (info.GitBranch, info.GitCommit) = GitInfoReader.Read(info.Cwd);
            result.Add(info);
        }

        // Drop usage state for sessions that disappeared, so memory doesn't grow.
        var liveIds = result.Select(r => r.SessionId).ToHashSet();
        foreach (var stale in _usage.Keys.Where(k => !liveIds.Contains(k)).ToList())
            _usage.Remove(stale);

        return result
            // If two registry files ever claim the same session, keep the freshest.
            .GroupBy(s => s.SessionId)
            .Select(g => g.OrderByDescending(s => s.UpdatedAt).First())
            .OrderBy(s => s.IsDormant)
            .ThenByDescending(s => s.IsBusy)
            .ThenByDescending(s => s.LastActivity ?? s.UpdatedAt)
            .ToList();
    }

    private static SessionInfo? TryParseRegistryFile(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            return new SessionInfo
            {
                Pid = r.TryGetProperty("pid", out var pid) ? pid.GetInt32() : 0,
                SessionId = GetString(r, "sessionId"),
                Cwd = GetString(r, "cwd"),
                Name = GetString(r, "name"),
                Entrypoint = GetString(r, "entrypoint"),
                Status = GetString(r, "status"),
                Version = GetString(r, "version"),
                StartedAt = GetEpochMs(r, "startedAt"),
                UpdatedAt = GetEpochMs(r, "updatedAt"),
            };
        }
        catch (Exception)
        {
            // Truncated/partial write or malformed file — skip this cycle.
            return null;
        }
    }

    private static string GetString(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static DateTimeOffset GetEpochMs(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.TryGetInt64(out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : DateTimeOffset.MinValue;

    private static bool IsProcessAlive(SessionInfo info)
    {
        if (info.Pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(info.Pid);
            if (p.HasExited) return false;
            // Guard against PID reuse: the process must have started at (or
            // before) the session's own start time, within a small tolerance.
            try
            {
                if (p.StartTime.ToUniversalTime() > info.StartedAt.UtcDateTime.AddSeconds(30))
                    return false;
            }
            catch (Exception)
            {
                // Access denied to StartTime — accept on PID existence alone.
            }
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    // ---------------------------------------------------------------------
    // Transcript usage tailing
    // ---------------------------------------------------------------------

    private sealed class UsageState
    {
        public string? TranscriptPath;
        public long Offset;
        public long OutputTokens;
        public long ContextTokens;
        public string? Model;
        public string? Slug;
        public DateTimeOffset? LastActivity;
        public DateTime LastPathSearch = DateTime.MinValue;
    }

    private void EnrichWithUsage(SessionInfo info)
    {
        if (!_usage.TryGetValue(info.SessionId, out var state))
            _usage[info.SessionId] = state = new UsageState();

        state.TranscriptPath ??= LocateTranscript(info, state);
        if (state.TranscriptPath is not null)
        {
            try { TailTranscript(state); }
            catch (IOException) { /* transient share violation — retry next tick */ }
        }

        info.OutputTokens = state.OutputTokens;
        info.ContextTokens = state.ContextTokens;
        info.Model = state.Model;
        info.Slug = state.Slug;
        info.LastActivity = state.LastActivity;
    }

    private string? LocateTranscript(SessionInfo info, UsageState state)
    {
        // Fast path: the project folder slug is the cwd with every
        // non-alphanumeric character replaced by '-'.
        var slug = Regex.Replace(info.Cwd, "[^A-Za-z0-9]", "-");
        var derived = Path.Combine(_projectsDir, slug, info.SessionId + ".jsonl");
        if (File.Exists(derived)) return derived;

        // Fallback: search all project folders, at most once per minute.
        if (DateTime.UtcNow - state.LastPathSearch < TimeSpan.FromMinutes(1)) return null;
        state.LastPathSearch = DateTime.UtcNow;
        try
        {
            return Directory
                .EnumerateFiles(_projectsDir, info.SessionId + ".jsonl", SearchOption.AllDirectories)
                .FirstOrDefault();
        }
        catch (Exception) { return null; }
    }

    private static void TailTranscript(UsageState state)
    {
        using var fs = new FileStream(state.TranscriptPath!, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        if (fs.Length < state.Offset) state.Offset = 0; // file replaced/truncated
        if (fs.Length == state.Offset) return;          // nothing new

        fs.Seek(state.Offset, SeekOrigin.Begin);
        var buf = new byte[fs.Length - state.Offset];
        int read = 0;
        while (read < buf.Length)
        {
            int n = fs.Read(buf, read, buf.Length - read);
            if (n == 0) break;
            read += n;
        }

        // Only consume complete lines; a partial trailing line is left for the
        // next tick by not advancing the offset past the final newline.
        int lastNewline = Array.LastIndexOf(buf, (byte)'\n', read - 1);
        if (lastNewline < 0) return;
        state.Offset += lastNewline + 1;

        var text = Encoding.UTF8.GetString(buf, 0, lastNewline + 1);
        foreach (var line in text.Split('\n'))
        {
            if (line.Length < 2 || !line.Contains("\"usage\"")) continue;
            try { ApplyUsageLine(line, state); }
            catch (JsonException) { /* not a well-formed record — ignore */ }
        }
    }

    private static void ApplyUsageLine(string line, UsageState state)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (!root.TryGetProperty("message", out var msg)) return;
        if (!msg.TryGetProperty("usage", out var usage)) return;

        long input = GetLong(usage, "input_tokens");
        long cacheCreate = GetLong(usage, "cache_creation_input_tokens");
        long cacheRead = GetLong(usage, "cache_read_input_tokens");
        long output = GetLong(usage, "output_tokens");

        state.OutputTokens += output;
        // The most recent request's total input approximates current context size.
        state.ContextTokens = input + cacheCreate + cacheRead;

        if (msg.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String)
            state.Model = m.GetString();
        if (root.TryGetProperty("slug", out var s) && s.ValueKind == JsonValueKind.String)
            state.Slug = s.GetString();
        if (root.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(ts.GetString(), out var when))
            state.LastActivity = when;
    }

    private static long GetLong(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
}
