namespace VoiceTranscript.Core.Domain;

/// <summary>The two kinds of work worth measuring.</summary>
public static class ProcessingStage
{
    /// <summary>Turning audio into text. Measured in speed against real time.</summary>
    public const string Transcribe = "transcribe";

    /// <summary>Turning text into a summary and ledger entries. Measured in tokens.</summary>
    public const string Analyse = "analyse";

    /// <summary>
    /// Answering a question about the archive.
    ///
    /// Its own stage rather than folded into analysis, because it is spent differently: analysis
    /// happens once per call whether anyone is watching, and this happens because somebody asked.
    /// Told apart, a large bill can be traced to whichever of the two caused it.
    /// </summary>
    public const string Ask = "ask";

    /// <summary>
    /// Reading one conversation for contradictions, evasion and timeline gaps.
    ///
    /// Its own stage for the same reason Ask is: it runs because somebody pressed the button
    /// (or opted into running it after every analysis), and a surprising bill must be traceable
    /// to the feature that produced it.
    /// </summary>
    public const string Consistency = "consistency";
}

/// <summary>
/// What one stage has cost so far.
///
/// Deliberately a sum rather than a list. The interesting questions here are aggregate — how many
/// hours of audio has this machine actually processed, how fast, and how many tokens has it spent
/// doing it — and answering them by loading every run and adding it up in the interface would
/// grow slower with every call recorded.
///
/// Plain settable properties rather than init-only, because Dapper materialises these straight
/// from the aggregate query.
/// </summary>
public record UsageTotals
{
    public int Runs { get; set; }
    public int Failures { get; set; }

    /// <summary>Wall-clock time spent working.</summary>
    public long ElapsedMs { get; set; }

    /// <summary>Length of the audio behind that work. Zero for stages that do not read audio.</summary>
    public long AudioMs { get; set; }

    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }

    public TimeSpan Elapsed => TimeSpan.FromMilliseconds(ElapsedMs);
    public TimeSpan Audio => TimeSpan.FromMilliseconds(AudioMs);

    public long TotalTokens => PromptTokens + CompletionTokens;

    /// <summary>Whether the provider reported any tokens at all — zeros are "not reported", not "free".</summary>
    public bool HasTokens => TotalTokens > 0;

    public bool IsEmpty => Runs == 0;

    /// <summary>
    /// How many times faster than real time the work ran. Null when there was no audio.
    ///
    /// The single most useful number this application can show about its own behaviour. Below one
    /// means an hour of conversation takes more than an hour to transcribe, which is the
    /// difference between a recorder that keeps up and one that falls permanently behind — and it
    /// is not otherwise visible anywhere, because a machine grinding through a backlog looks
    /// exactly like one that has hung.
    /// </summary>
    public double? SpeedFactor =>
        ElapsedMs > 0 && AudioMs > 0 ? (double)AudioMs / ElapsedMs : null;
}

/// <summary>
/// One day's work, for a chart.
///
/// Days with nothing in them are included and left at zero — a chart drawn only from days that
/// have rows compresses a fortnight of silence into nothing and makes a sporadic week look
/// continuous, which is the opposite of what the chart is for.
/// </summary>
public sealed record DailyUsage
{
    public DateOnly Day { get; set; }
    public int Runs { get; set; }
    public long ElapsedMs { get; set; }
    public long AudioMs { get; set; }
    public long Tokens { get; set; }

    public TimeSpan Audio => TimeSpan.FromMilliseconds(AudioMs);
    public TimeSpan Elapsed => TimeSpan.FromMilliseconds(ElapsedMs);

    /// <summary>Short label for the axis: "3 Eyl".</summary>
    public string Label => Day.ToString("d MMM");

    public bool IsEmpty => Runs == 0;
}

/// <summary>The same totals, for one engine, so two of them can be compared.</summary>
public sealed record EngineUsage : UsageTotals
{
    public string Engine { get; set; } = "";
}

/// <summary>
/// The last thing that processed one call, and how it went.
///
/// Shown on the processing screen because "yazıya dökülüyor" answers what is happening and not
/// what is doing it — and on this product those are different questions with different fixes. A
/// call transcribed locally at a fifth of real time and one sent to a hosted model look identical
/// in a list, right up until somebody asks why one took four hours.
/// </summary>
public sealed record CallRun
{
    public long CallId { get; set; }
    public string Engine { get; set; } = "";
    public long ElapsedMs { get; set; }
    public long AudioMs { get; set; }
    public bool Succeeded { get; set; }

    public TimeSpan Elapsed => TimeSpan.FromMilliseconds(ElapsedMs);

    /// <summary>How many times faster than real time this one call ran. Null without audio.</summary>
    public double? SpeedFactor =>
        ElapsedMs > 0 && AudioMs > 0 ? (double)AudioMs / ElapsedMs : null;
}
