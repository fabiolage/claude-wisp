using System;
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
    int _failures;

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

        _refreshTimer.Interval = TimeSpan.FromSeconds(Math.Max(30, _settings.RefreshSeconds));
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _refreshTimer.Start();

        _tickTimer.Tick += (_, _) => Tick();
        _tickTimer.Start();

        _hoverTimer.Tick += (_, _) => CheckHover();
        _hoverTimer.Start();

        SystemEvents_DisplayChanged();
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

    public async Task RefreshAsync()
    {
        if (!await _fetchLock.WaitAsync(0)) return;
        try
        {
            PulseOnce();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var snap = await _usage.FetchAsync(_settings.SessionKey, cts.Token);
            _last = snap;
            _failures = 0;
            _refreshTimer.Interval = TimeSpan.FromSeconds(Math.Max(30, _settings.RefreshSeconds));
            Render(snap, null);
        }
        catch (Exception e)
        {
            _failures++;
            // Back off gently on repeated failures (max 5 minutes).
            _refreshTimer.Interval = TimeSpan.FromSeconds(Math.Min(300, Math.Max(30, _settings.RefreshSeconds) * _failures));
            Render(_last, e is UsageException ? e.Message : "Something went wrong");
        }
        finally
        {
            if (_hwnd != IntPtr.Zero) Native.KeepOnTop(_hwnd);
            _fetchLock.Release();
        }
    }

    void Tick()
    {
        if (_last == null) return;
        // When a window resets, fetch right away instead of waiting for the next poll.
        if (_last.Session?.ResetsAt is { } r && r <= DateTimeOffset.Now)
            _ = RefreshAsync();
        else
            ResetText.Text = FormatReset(_last.Session?.ResetsAt);
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

        var session = snap.Session?.Percent ?? 0;
        PercentText.Text = Math.Round(session).ToString("0");
        ResetCaption.Text = snap.Session?.ResetsAt == null ? "session" : "resets in";
        ResetText.Text = FormatReset(snap.Session?.ResetsAt);
        LabelText.Text = "5h session";

        var accent = AccentFor(session);
        AnimateWidth(SessionBar, SessionTrack.ActualWidth > 0 ? SessionTrack.ActualWidth : 196, session);
        SessionAccent.Color = accent;
        SessionGlow.Color = accent;

        if (snap.Week is { } week)
        {
            WeekRow.Visibility = Visibility.Visible;
            WeekText.Text = $"{Math.Round(week.Percent):0}%";
            AnimateWidth(WeekBar, WeekTrack.ActualWidth > 0 ? WeekTrack.ActualWidth : 120, week.Percent);
        }
        else
        {
            WeekRow.Visibility = Visibility.Collapsed;
        }

        Updated?.Invoke(snap, error);
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

    public static string FormatReset(DateTimeOffset? at)
    {
        if (at == null) return "—";
        var left = at.Value - DateTimeOffset.Now;
        if (left <= TimeSpan.Zero) return "now";
        if (left.TotalDays >= 1) return $"{(int)left.TotalDays}d {left.Hours}h";
        if (left.TotalHours >= 1) return $"{(int)left.TotalHours}h {left.Minutes:00}m";
        return $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))}m";
    }

    protected override void OnClosed(EventArgs e)
    {
        _usage.Dispose();
        base.OnClosed(e);
    }
}
