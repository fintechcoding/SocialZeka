using Microsoft.Win32;
using VoiceTranscript.Core.Configuration;

namespace VoiceTranscript.App.Services;

/// <summary>
/// Looks for a newer release again while the application stays running.
///
/// The check used to run once, at startup, and this application is meant to sit in the tray for
/// weeks. So a copy left open since Tuesday afternoon never learned about Wednesday's release —
/// the one carrying the fixes for the slowness its user was still reporting on Thursday. The
/// user's rule is unchanged: <b>it checks and asks; it never installs on its own.</b> This class
/// decides only <i>when</i> the same check runs again. What it finds goes down the same path as
/// the startup check and becomes the same offer, nothing louder; what it does not find becomes
/// a log line and a moved "Son denetim" stamp.
///
/// <b>Once a day.</b> Releases come out a few times a month at most, GitHub's unauthenticated
/// limit is sixty requests an hour per address and is shared with everything else on it, and the
/// cost of waiting is bounded by the cadence: a copy that has been open for a month learns of a
/// release within a day of it. Hourly would be twenty-four lines a day in this log and in
/// GitHub's, for a release that is not there twenty-three of those times.
///
/// Three things shape how the day is measured:
///
///   <b>It never runs while a call is being recorded or transcribed.</b> The recorder is asked
///   nothing and told nothing: the scheduler reads the two properties it already publishes and,
///   if either says busy, waits. A call that overlaps the due moment postpones the check until
///   the recorder announces idle — which it already does through its state event — or until the
///   next tick, whichever is first.
///
///   <b>It survives sleep.</b> "Due" is a wall-clock comparison against the stamp of the last
///   check, evaluated on a short tick and again the moment Windows reports a resume. A laptop
///   closed for a week checks on wake because on wake the stamp is a week old — not because a
///   timer counted a day of sleep, which no timer is relied on to do.
///
///   <b>It is exactly as loud as the startup check.</b> Every failure is swallowed, one check
///   runs at a time, and a check that got no answer — the network not yet back after a resume is
///   the common case — is tried again within the hour rather than stamped as done for the day.
/// </summary>
public sealed class UpdateScheduler : IDisposable
{
    /// <summary>How long after a check the next one is due.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>
    /// How long after a check that got no answer the next attempt is made.
    ///
    /// The first check after a resume runs on a network that is often seconds from existing. If
    /// that failure counted as the day's check, a laptop opened in the morning would learn of a
    /// release the next morning — the same day late this class exists to end.
    /// </summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);

    /// <summary>
    /// How often "is it due" is evaluated. The tick does no I/O: it compares two timestamps and
    /// reads two properties. It is what catches a resume Windows did not announce and a call
    /// that ended without the state event reaching here.
    /// </summary>
    public static readonly TimeSpan Tick = TimeSpan.FromMinutes(15);

    /// <summary>How long after a resume the check waits for the radio to come back after the screen.</summary>
    public static readonly TimeSpan ResumeGrace = TimeSpan.FromSeconds(45);

    private readonly UpdateService _updates;
    private readonly Func<AppSettings> _settings;
    private readonly Action<AppSettings> _save;
    private readonly Func<bool> _recorderIsIdle;
    private readonly Func<UpdateCheck, Task> _deliver;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _retry;
    private readonly TimeSpan _resumeGrace;

    /// <summary>One check at a time. A tick, a resume and an idle notice can all land together.</summary>
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    private readonly Lock _state = new();
    private DateTimeOffset? _retryAt;
    private bool _deferralLogged;

    private CancellationTokenSource? _loop;
    private bool _watchingPower;

    /// <param name="updates">The client that does the actual asking. Real; the network behind it need not be.</param>
    /// <param name="settings">The switch and the stamp, read fresh every time so a change on the tab takes effect at the next tick.</param>
    /// <param name="save">Writes the stamp back. Whatever refreshes the tab hangs off this.</param>
    /// <param name="recorderIsIdle">Nothing recording, nothing transcribing. Read from what the recorder already exposes.</param>
    /// <param name="deliver">What becomes of a result. The startup check's own path, so the two cannot disagree.</param>
    /// <param name="clock">Now. Replaceable so a week of sleep can be tested in a millisecond.</param>
    public UpdateScheduler(
        UpdateService updates,
        Func<AppSettings> settings,
        Action<AppSettings> save,
        Func<bool> recorderIsIdle,
        Func<UpdateCheck, Task> deliver,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? interval = null,
        TimeSpan? retryAfterFailure = null,
        TimeSpan? resumeGrace = null)
    {
        _updates = updates;
        _settings = settings;
        _save = save;
        _recorderIsIdle = recorderIsIdle;
        _deliver = deliver;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _interval = interval ?? Interval;
        _retry = retryAfterFailure ?? RetryAfterFailure;
        _resumeGrace = resumeGrace ?? ResumeGrace;
    }

    /// <summary>
    /// Whether a check is owed now.
    ///
    /// The switch first: off means off, whatever the stamp says. Then a pending retry, which is
    /// shorter than the day and would otherwise be hidden behind the stamp the failed attempt
    /// wrote. Then the day itself, measured from the last check of any kind — button, startup
    /// or this — so a manual check this morning is not followed by an automatic one at noon.
    /// </summary>
    public bool IsDue()
    {
        var settings = _settings();
        if (!settings.CheckForUpdates) return false;

        var now = _clock();

        lock (_state)
        {
            if (_retryAt is { } retry) return now >= retry;
        }

        return settings.LastUpdateCheck is not { } last || now - last >= _interval;
    }

    /// <summary>
    /// One evaluation: due, idle, check. Safe to call from anywhere, any number of times.
    /// </summary>
    /// <returns>True when a check actually ran.</returns>
    public async Task<bool> TickAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!IsDue()) return false;

            if (!_recorderIsIdle())
            {
                // Said once per deferral, not once per tick: a two-hour transcription is eight
                // ticks, and eight identical lines teach the reader to skip the ninth.
                lock (_state)
                {
                    if (!_deferralLogged)
                    {
                        AppLog.Write("güncelleme", "denetim ertelendi: kayıt ya da çözümleme sürüyor");
                        _deferralLogged = true;
                    }
                }

                return false;
            }

            return await CheckNowAsync(cancellationToken) is not null;
        }
        catch (Exception e)
        {
            AppLog.Error("güncelleme", e, "zamanlanmış denetimde beklenmeyen hata");
            return false;
        }
    }

    /// <summary>
    /// A check now, schedule or no schedule. Startup uses this for the check it has always made.
    ///
    /// Stamps before delivering, because delivering may open the offer and the offer waits for a
    /// human; the stamp is the record that the check happened, not that it was read.
    /// </summary>
    /// <returns>What was found, or null when a check was already in flight or went wrong.</returns>
    public async Task<UpdateCheck?> CheckNowAsync(CancellationToken cancellationToken = default)
    {
        if (!await _oneAtATime.WaitAsync(0, cancellationToken)) return null;

        try
        {
            var check = await _updates.CheckAsync(cancellationToken);
            var now = _clock();

            lock (_state)
            {
                _retryAt = check.Failed ? now + _retry : null;
                _deferralLogged = false;
            }

            _save(_settings() with { LastUpdateCheck = now });

            await _deliver(check);

            return check;
        }
        catch (Exception e)
        {
            // Never allowed to matter. This is a courtesy running beside the thing the
            // application is actually for.
            AppLog.Error("güncelleme", e, "denetim sırasında beklenmeyen hata");
            return null;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>
    /// The recorder has gone idle. A check postponed by a call runs now rather than at the next
    /// tick — the tick would get there, but a quarter of an hour after the call is a stranger
    /// moment for an offer than the minute after it.
    /// </summary>
    public Task NotifyIdleAsync() => TickAsync();

    /// <summary>
    /// Windows is back from sleep or hibernation. The stamp is whatever it was when the lid
    /// closed, so if a day has passed in between this is where the week-old laptop catches up.
    /// </summary>
    public async Task ResumedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // The radio comes back after the screen. A check fired at the first instant of a
            // resume fails on a network that is a few seconds from existing.
            if (_resumeGrace > TimeSpan.Zero) await Task.Delay(_resumeGrace, cancellationToken);

            await TickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Shutting down. Nothing to say.
        }
    }

    /// <summary>
    /// Starts the tick and listens for resume. Idempotent; the startup check itself is not
    /// started here — it is the caller's, on the caller's delay.
    /// </summary>
    public void Start()
    {
        if (_loop is not null) return;

        _loop = new CancellationTokenSource();
        _ = RunTicksAsync(_loop.Token);

        try
        {
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            _watchingPower = true;
        }
        catch (Exception e)
        {
            // A host without a message pump refuses the hook. The tick still runs, and on
            // Windows a due timer fires promptly on wake anyway — the resume hook only makes it
            // prompt to the second rather than to the quarter-hour.
            AppLog.Error("güncelleme", e, "uyanma olayı dinlenemedi; yalnız zamanlayıcıyla denetlenecek");
        }
    }

    private async Task RunTicksAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(Tick);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await TickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;

        // Raised on the SystemEvents thread, which must be given back at once.
        _ = ResumedAsync(_loop?.Token ?? CancellationToken.None);
    }

    public void Dispose()
    {
        if (_watchingPower)
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _watchingPower = false;
        }

        _loop?.Cancel();
        _loop?.Dispose();
        _loop = null;
    }
}
