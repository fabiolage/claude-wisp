using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Wisp;

public partial class MainWindow : Window
{
    static readonly Color Calm = Color.FromRgb(0xCF, 0xE0, 0xFF);   // pale spectral blue
    static readonly Color Warm = Color.FromRgb(0xF2, 0xC3, 0x8B);   // amber
    static readonly Color Hot = Color.FromRgb(0xE8, 0x83, 0x6B);    // Claude-ish coral

    readonly Settings _settings;
    readonly UsageService _usage = new();
    readonly DispatcherTimer _refreshTimer = new();
    readonly DispatcherTimer _tickTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    readonly SemaphoreSlim _fetchLock = new(1, 1);

    IntPtr _hwnd;
    UsageSnapshot? _last;
    bool _hovering;
    DateTimeOffset? _lastApiAttempt;
    FileSystemWatcher? _feedWatcher;

    public event Action<UsageSnapshot?, string?>? Updated;

    public MainWindow(Settings settings)
    {
        _settings = settings;
        InitializeComponent();
        Opacity = 0;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeGhost(_hwnd);
        };
        SizeChanged += (_, _) => PlaceInCorner();
        Loaded += async (_, _) =>
        {
            PlaceInCorner();
            FadeTo(_settings.Opacity, 900);
            await RefreshAsync();
        };

        _refreshTimer.Interval = TimeSpan.FromSeconds(Math.Max(15, _settings.RefreshSeconds));
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _refreshTimer.Start();

        _tickTimer.Tick += (_, _) => Tick();
        _tickTimer.Start();

        _hoverTimer.Tick += (_, _) => CheckHover();
        _hoverTimer.Start();

        SystemEvents_DisplayChanged();
        WatchFeed();
    }

    void WatchFeed()
    {
        try
        {
            Directory.CreateDirectory(ClaudeCodeFeed.FeedDirectory);
            _feedWatcher = new FileSystemWatcher(ClaudeCodeFeed.FeedDirectory, ClaudeCodeFeed.FeedFileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
            };
            FileSystemEventHandler changed = (_, _) => Dispatcher.BeginInvoke(OnFeedChanged);
            _feedWatcher.Changed += changed;
            _feedWatcher.Created += changed;
            _feedWatcher.Renamed += (_, _) => Dispatcher.BeginInvoke(OnFeedChanged);
            _feedWatcher.EnableRaisingEvents = true;
        }
        catch { /* the minute timer still picks the feed up */ }
    }

    public UsageSnapshot? Last => _last;

    // ---------- placement & visibility ----------

    public void SetCorner(Corner corner)
    {
        _settings.Corner = corner;
        _settings.Save();
        PlaceInCorner();
    }

    public void SetRestingOpacity(double opacity)
    {
        _settings.Opacity = opacity;
        _settings.Save();
        if (!_hovering) FadeTo(opacity, 250);
    }

    void PlaceInCorner()
    {
        var area = SystemParameters.WorkArea;
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        Left = _settings.Corner is Corner.TopLeft or Corner.BottomLeft ? area.Left : area.Right - w;
        Top = _settings.Corner is Corner.TopLeft or Corner.TopRight ? area.Top : area.Bottom - h;
    }

    void SystemEvents_DisplayChanged()
    {
        // Work area changes when the taskbar moves or a monitor is (un)plugged.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(PlaceInCorner);
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.WorkArea)) Dispatcher.BeginInvoke(PlaceInCorner);
        };
    }

    void CheckHover()
    {
        if (_hwnd == IntPtr.Zero || !IsVisible) return;
        var inside = _settings.FadeOnHover && Native.CursorInside(_hwnd);
        if (inside == _hovering) return;
        _hovering = inside;
        FadeTo(inside ? 0.06 : _settings.Opacity, inside ? 180 : 600);
    }

    void FadeTo(double to, int ms) =>
        BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        });

    // ---------- data ----------

    /// <summary>
    /// Reads Claude Code's feed (free, local) every tick. Only when that's missing or stale does it ask
    /// Anthropic, and then at most every <see cref="Settings.ApiMinutes"/> and never during a cooldown.
    /// </summary>
    public async Task RefreshAsync(bool force = false)
    {
        if (!await _fetchLock.WaitAsync(0)) return;
        try
        {
            PulseOnce();
            var best = Newest(_last, ClaudeCodeFeed.Read());
            string? error = null;

            var now = DateTimeOffset.Now;
            var feedFresh = best is { Source: "Claude Code" } && now - best.FetchedAt < TimeSpan.FromMinutes(3);
            var apiDue = force || _lastApiAttempt == null ||
                         now - _lastApiAttempt >= TimeSpan.FromMinutes(Math.Max(2, _settings.ApiMinutes));

            if (!feedFresh && apiDue)
            {
                _lastApiAttempt = now;
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var snap = await _usage.FetchAsync(_settings.SessionKey, _settings.OAuthBlockedUntil, until =>
                    {
                        _settings.OAuthBlockedUntil = until;
                        _settings.Save();
                    }, cts.Token);
                    best = Newest(best, snap);
                }
                catch (RateLimitedException e)
                {
                    // With numbers from Claude Code already on screen, a cooldown isn't worth mentioning.
                    if (best == null)
                        error = $"Anthropic paused updates · {Format.Duration(e.RetryAfter)}";
                }
                catch (Exception e)
                {
                    error = e is UsageException ? e.Message : "Something went wrong";
                }
            }

            _last = best;
            Render(best, error);
        }
        finally
        {
            if (_hwnd != IntPtr.Zero) Native.KeepOnTop(_hwnd);
            _fetchLock.Release();
        }
    }

    /// <summary>Claude Code just wrote new numbers; show them right away.</summary>
    void OnFeedChanged()
    {
        var feed = ClaudeCodeFeed.Read();
        if (feed == null || (_last != null && feed.FetchedAt <= _last.FetchedAt)) return;
        _last = feed;
        Render(feed, null);
    }

    static UsageSnapshot? Newest(UsageSnapshot? a, UsageSnapshot? b) =>
        a == null ? b : b == null ? a : b.FetchedAt > a.FetchedAt ? b : a;

    /// <summary>A window whose reset time has passed is back to zero, even if we haven't heard so yet.</summary>
    static UsageWindow? Current(UsageWindow? w) =>
        w is { ResetsAt: { } r } && r <= DateTimeOffset.Now ? w with { Percent = 0, ResetsAt = null } : w;

    void Tick()
    {
        if (_last == null) return;
        if (_last.Session?.ResetsAt is { } r && r <= DateTimeOffset.Now)
        {
            Render(_last, null);
            _ = RefreshAsync();
        }
        else
        {
            ResetText.Text = Format.Reset(_last.Session?.ResetsAt);
            LabelText.Text = AgeLabel(_last);
        }
    }

    static string AgeLabel(UsageSnapshot snap)
    {
        var age = DateTimeOffset.Now - snap.FetchedAt;
        return age < TimeSpan.FromMinutes(15) ? "5h session" : $"as of {Format.Ago(age)} ago";
    }

    void Render(UsageSnapshot? snap, string? error)
    {
        StatusText.Text = error ?? "";
        StatusText.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;

        if (snap == null)
        {
            PercentText.Text = "··";
            ResetText.Text = "—";
            WeekText.Text = "—";
            Updated?.Invoke(null, error);
            return;
        }

        var sessionWindow = Current(snap.Session);
        var session = sessionWindow?.Percent ?? 0;
        PercentText.Text = Math.Round(session).ToString("0");
        ResetCaption.Text = sessionWindow?.ResetsAt == null ? "window" : "resets in";
        ResetText.Text = sessionWindow?.ResetsAt == null ? "fresh" : Format.Reset(sessionWindow.ResetsAt);
        LabelText.Text = AgeLabel(snap);

        var accent = AccentFor(session);
        AnimateWidth(SessionBar, SessionTrack.ActualWidth > 0 ? SessionTrack.ActualWidth : 196, session);
        SessionAccent.Color = accent;
        SessionGlow.Color = accent;

        if (Current(snap.Week) is { } week)
        {
            WeekRow.Visibility = Visibility.Visible;
            WeekText.Text = $"{Math.Round(week.Percent):0}%";
            AnimateWidth(WeekBar, WeekTrack.ActualWidth > 0 ? WeekTrack.ActualWidth : 120, week.Percent);
        }
        else
        {
            WeekRow.Visibility = Visibility.Collapsed;
        }

        Updated?.Invoke(snap with { Session = sessionWindow, Week = Current(snap.Week) }, error);
    }

    static void AnimateWidth(FrameworkElement bar, double trackWidth, double percent) =>
        bar.BeginAnimation(WidthProperty, new DoubleAnimation(trackWidth * percent / 100.0, TimeSpan.FromMilliseconds(700))
        {
            EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut },
        });

    void PulseOnce() =>
        Pulse.BeginAnimation(OpacityProperty, new DoubleAnimation(0.15, 0.7, TimeSpan.FromMilliseconds(900))
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });

    static Color AccentFor(double pct) =>
        pct < 50 ? Calm
        : pct < 80 ? Lerp(Calm, Warm, (pct - 50) / 30)
        : Lerp(Warm, Hot, Math.Min(1, (pct - 80) / 15));

    static Color Lerp(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));

    protected override void OnClosed(EventArgs e)
    {
        _usage.Dispose();
        _feedWatcher?.Dispose();
        base.OnClosed(e);
    }
}
