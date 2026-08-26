using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace ClaudePulse.Models;

/// <summary>Bindable wrapper over <see cref="SessionInfo"/>, updated in place each poll.</summary>
public sealed class SessionViewModel : INotifyPropertyChanged
{
    public string SessionId { get; }
    private SessionInfo _info;

    public SessionViewModel(SessionInfo info)
    {
        SessionId = info.SessionId;
        _info = info;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(SessionInfo info)
    {
        _info = info;
        // Everything is derived from _info; refresh all bindings at once.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    /// <summary>Called on a timer so elapsed time ticks even with no data change.</summary>
    public void RefreshClock() => Raise(nameof(Elapsed));

    public int Pid => _info.Pid;
    public bool IsBusy => _info.IsBusy;
    public bool IsDormant => _info.IsDormant;
    public string Name => _info.Name;
    public string FolderLeaf => _info.FolderLeaf;
    public string StatusText => _info.EffectiveStatus;

    /// <summary>
    /// Middle-truncated name: sessions in the same folder differ only by a
    /// short suffix, so the tail must survive truncation.
    /// </summary>
    public string DisplayName
    {
        get
        {
            var n = _info.Name;
            return n.Length <= 26 ? n : $"{n[..19]}…{n[^6..]}";
        }
    }
    public string EntrypointBadge => _info.Entrypoint switch
    {
        "cli" => "CLI",
        "claude-desktop" => "Desktop",
        "" => "?",
        _ => _info.Entrypoint,
    };

    public string Elapsed => FormatElapsed(DateTimeOffset.Now - _info.StartedAt);

    public string UsageText
    {
        get
        {
            if (_info.ContextTokens == 0 && _info.OutputTokens == 0) return "no usage yet";
            return $"ctx {FormatTokens(_info.ContextTokens)} · out {FormatTokens(_info.OutputTokens)}";
        }
    }

    public string ModelText => ShortModel(_info.Model);

    public string BranchText
    {
        get
        {
            if (_info.GitBranch is null && _info.GitCommit is null) return "";
            var branch = _info.GitBranch ?? "?";
            return _info.GitCommit is null ? $"⎇ {branch}" : $"⎇ {branch} @ {_info.GitCommit}";
        }
    }

    // ------------------------------------------------------------- usage bars

    private static readonly Brush BarGreen = MakeFrozen("#33D17A");
    private static readonly Brush BarAmber = MakeFrozen("#E5A50A");
    private static readonly Brush BarRed = MakeFrozen("#ED5353");
    private static readonly Brush BarBlue = MakeFrozen("#6C9DF2");

    private long _outputScale = 1;

    /// <summary>1M-context models advertise it in the model id; everything else is 200k.</summary>
    private long ContextLimit =>
        _info.Model?.Contains("[1m]", StringComparison.OrdinalIgnoreCase) == true ? 1_000_000 : 200_000;

    public double ContextPercent => Math.Min(100.0, _info.ContextTokens * 100.0 / ContextLimit);
    public double OutputPercent => Math.Min(100.0, _info.OutputTokens * 100.0 / _outputScale);

    public Brush CtxBarBrush => ContextPercent switch
    {
        < 50 => BarGreen,
        < 80 => BarAmber,
        _ => BarRed,
    };

    public Brush OutBarBrush => BarBlue;

    public string CtxBarTooltip =>
        $"context: {FormatTokens(_info.ContextTokens)} of {FormatTokens(ContextLimit)} ({ContextPercent:0}% full)";
    public string OutBarTooltip =>
        $"output tokens generated: {FormatTokens(_info.OutputTokens)} (bar is relative to your largest session)";

    /// <summary>Shared scale so output bars compare sessions against each other.</summary>
    public void SetOutputScale(long maxOutputAcrossSessions)
    {
        long scale = Math.Max(1, maxOutputAcrossSessions);
        if (scale == _outputScale) return;
        _outputScale = scale;
        Raise(nameof(OutputPercent));
        Raise(nameof(OutBarTooltip));
    }

    private static Brush MakeFrozen(string hex)
    {
        var b = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public string Tooltip =>
        $"{_info.Name} — {_info.EffectiveStatus}\n" +
        $"{_info.Cwd}\n" +
        $"session {_info.SessionId}" + (_info.Slug is null ? "" : $"  ({_info.Slug})") + "\n" +
        $"pid {_info.Pid} · Claude Code v{_info.Version}\n" +
        "Click to bring this session's window to the front";

    private static string FormatElapsed(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m";
        return "<1m";
    }

    private static string FormatTokens(long n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:0.0}M",
        >= 10_000 => $"{n / 1000}k",
        >= 1_000 => $"{n / 1000.0:0.0}k",
        _ => n.ToString(),
    };

    private static string ShortModel(string? model)
    {
        if (string.IsNullOrEmpty(model)) return "";
        var m = model.Replace("claude-", "");
        int cut = m.IndexOf("-2", StringComparison.Ordinal); // strip date suffix like -20251001
        return cut > 0 ? m[..cut] : m;
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
