using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ClaudePulse.Models;

namespace ClaudePulse.Services;

/// <summary>
/// Continuously records which CLI sessions are alive; after a reboot (or any
/// time sessions died while ClaudePulse was off) the leftover entries can be
/// relaunched via `claude --resume` in Windows Terminal tabs.
/// </summary>
public static class RestoreService
{
    public sealed record Entry(string SessionId, string Cwd, string Name);
    public sealed record SavedSet(DateTimeOffset SavedAt, List<Entry> Entries);

    private static readonly string RestorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudePulse", "restore.json");

    private static readonly string SetPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudePulse", "saved-set.json");

    private static string? _lastWritten;

    public static List<Entry> LoadPending()
    {
        try
        {
            if (!File.Exists(RestorePath)) return new();
            return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(RestorePath)) ?? new();
        }
        catch (Exception) { return new(); }
    }

    /// <summary>
    /// Persist the current live CLI session set (only when it changed).
    /// Pending not-yet-restored entries are kept so they survive a ClaudePulse
    /// restart that happens before the user clicks Restore.
    /// </summary>
    public static void SaveCurrent(IEnumerable<SessionInfo> live, IEnumerable<Entry> pending)
    {
        try
        {
            var entries = live
                .Where(s => s.Entrypoint == "cli" && !string.IsNullOrEmpty(s.SessionId))
                .Select(s => new Entry(s.SessionId, s.Cwd, s.Name))
                .Concat(pending)
                .DistinctBy(e => e.SessionId)
                .OrderBy(e => e.SessionId)
                .ToList();
            var json = JsonSerializer.Serialize(entries);
            if (json == _lastWritten) return;
            Directory.CreateDirectory(Path.GetDirectoryName(RestorePath)!);
            File.WriteAllText(RestorePath, json);
            _lastWritten = json;
        }
        catch (Exception) { /* best-effort */ }
    }

    // ------------------------------------------------------ named session set

    /// <summary>Deliberately remember the current CLI sessions for later relaunch.</summary>
    public static int SaveSet(IEnumerable<SessionInfo> live)
    {
        var entries = live
            .Where(s => s.Entrypoint == "cli" && !string.IsNullOrEmpty(s.SessionId))
            .Select(s => new Entry(s.SessionId, s.Cwd, s.Name))
            .ToList();
        if (entries.Count == 0) return 0;
        Directory.CreateDirectory(Path.GetDirectoryName(SetPath)!);
        File.WriteAllText(SetPath, JsonSerializer.Serialize(
            new SavedSet(DateTimeOffset.Now, entries),
            new JsonSerializerOptions { WriteIndented = true }));
        return entries.Count;
    }

    public static SavedSet? LoadSet()
    {
        try
        {
            if (!File.Exists(SetPath)) return null;
            return JsonSerializer.Deserialize<SavedSet>(File.ReadAllText(SetPath));
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Opens one Windows Terminal tab per session running `claude --resume`.
    /// Falls back to separate cmd windows when wt.exe is unavailable.
    /// Returns how many sessions were launched.
    /// </summary>
    public static int Launch(IReadOnlyList<Entry> entries)
    {
        var valid = entries.Where(e => Directory.Exists(e.Cwd)).ToList();
        if (valid.Count == 0) return 0;

        var tabs = string.Join(" ; ", valid.Select(e =>
            $"new-tab --title \"{e.Name}\" -d \"{e.Cwd}\" cmd /k claude --resume {e.SessionId}"));
        try
        {
            Process.Start(new ProcessStartInfo("wt.exe", tabs) { UseShellExecute = true });
            return valid.Count;
        }
        catch (Exception)
        {
            // No Windows Terminal — plain console windows instead.
            int launched = 0;
            foreach (var e in valid)
            {
                try
                {
                    Process.Start(new ProcessStartInfo("cmd.exe", $"/k claude --resume {e.SessionId}")
                    {
                        WorkingDirectory = e.Cwd,
                        UseShellExecute = true,
                    });
                    launched++;
                }
                catch (Exception) { /* skip this one */ }
            }
            return launched;
        }
    }
}
