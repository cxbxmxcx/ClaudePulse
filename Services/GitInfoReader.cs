using System.IO;

namespace ClaudePulse.Services;

/// <summary>
/// Reads the current branch and commit for a working directory straight from
/// .git files — no git.exe processes, cheap enough to run every poll.
/// </summary>
public static class GitInfoReader
{
    public static (string? Branch, string? Commit) Read(string cwd)
    {
        try
        {
            // Walk up from cwd to find the repo root (.git dir, or .git file for worktrees).
            string? dir = cwd;
            string? gitPath = null;
            while (!string.IsNullOrEmpty(dir))
            {
                var candidate = Path.Combine(dir, ".git");
                if (Directory.Exists(candidate) || File.Exists(candidate)) { gitPath = candidate; break; }
                dir = Path.GetDirectoryName(dir);
            }
            if (gitPath is null) return (null, null);

            string gitDir = gitPath;
            if (File.Exists(gitPath)) // worktree: ".git" is a file pointing at the real gitdir
            {
                var content = File.ReadAllText(gitPath).Trim();
                if (!content.StartsWith("gitdir:")) return (null, null);
                gitDir = content["gitdir:".Length..].Trim();
                if (!Path.IsPathRooted(gitDir))
                    gitDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(gitPath)!, gitDir));
            }

            string headFile = Path.Combine(gitDir, "HEAD");
            if (!File.Exists(headFile)) return (null, null);
            string head = File.ReadAllText(headFile).Trim();

            // Worktrees keep shared refs in the main repo's .git (the "commondir").
            string commonDir = gitDir;
            string commonFile = Path.Combine(gitDir, "commondir");
            if (File.Exists(commonFile))
            {
                var c = File.ReadAllText(commonFile).Trim();
                commonDir = Path.IsPathRooted(c) ? c : Path.GetFullPath(Path.Combine(gitDir, c));
            }

            if (!head.StartsWith("ref:"))
                return ("detached", Short(head));

            string refName = head[4..].Trim(); // e.g. refs/heads/main
            string branch = refName.StartsWith("refs/heads/") ? refName["refs/heads/".Length..] : refName;

            string refFile = Path.Combine(commonDir, refName.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(refFile))
                return (branch, Short(File.ReadAllText(refFile).Trim()));

            string packed = Path.Combine(commonDir, "packed-refs");
            if (File.Exists(packed))
                foreach (var line in File.ReadLines(packed))
                    if (line.Length > 41 && line.EndsWith(" " + refName, StringComparison.Ordinal))
                        return (branch, Short(line[..40]));

            return (branch, null);
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    private static string Short(string hash) => hash.Length >= 7 ? hash[..7] : hash;
}
