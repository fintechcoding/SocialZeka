using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceTranscript.Core.Asr;
using VoiceTranscript.Core.Configuration;
using VoiceTranscript.Core.Llm;

namespace VoiceTranscript.App.ViewModels;

/// <summary>Whether a service answered when it was asked.</summary>
public enum ServiceReach
{
    /// <summary>Not asked yet, or the answer is stale.</summary>
    Unknown,

    /// <summary>Answered. Work sent here will be attempted.</summary>
    Reachable,

    /// <summary>Did not answer. Work will fall through to the next one, or be skipped.</summary>
    Unreachable,

    /// <summary>Runs on this machine, so there is nothing to reach.</summary>
    Local,

    /// <summary>Configured but missing something it needs — usually a key.</summary>
    Incomplete,
}

/// <summary>One service in a chain, with its place in the order.</summary>
/// <param name="Order">1-based. The order they will actually be tried in.</param>
/// <param name="IsActive">The one that will be used, all being well — the first usable.</param>
public sealed partial class AiServiceRow(
    int order, string name, string detail, bool isActive, bool sendsDataOffMachine) : ObservableObject
{
    public int Order { get; } = order;
    public string Name { get; } = name;
    public string Detail { get; } = detail;
    public bool IsActive { get; } = isActive;

    /// <summary>Whether using this one means the conversation leaves the machine.</summary>
    public bool SendsDataOffMachine { get; } = sendsDataOffMachine;

    [ObservableProperty] private ServiceReach _reach = ServiceReach.Unknown;

    public string ReachText => Reach switch
    {
        ServiceReach.Reachable => "bağlandı",
        ServiceReach.Unreachable => "yanıt vermiyor",
        ServiceReach.Local => "bu makinede",
        ServiceReach.Incomplete => "anahtar eksik",
        _ => "denenmedi",
    };

    public string ReachBrushKey => Reach switch
    {
        ServiceReach.Reachable or ServiceReach.Local => "SystemFillColorSuccessBrush",
        ServiceReach.Unreachable => "SystemFillColorCriticalBrush",
        ServiceReach.Incomplete => "SystemFillColorCautionBrush",
        _ => "TextFillColorTertiaryBrush",
    };

    partial void OnReachChanged(ServiceReach value)
    {
        OnPropertyChanged(nameof(ReachText));
        OnPropertyChanged(nameof(ReachBrushKey));
    }
}

/// <summary>
/// Which services will actually do the work, in the order they will be tried.
///
/// The application already made these decisions and kept them to itself. Transcription can run
/// locally or fall through an ordered list of hosted endpoints; analysis talks to whichever model
/// is configured, which may or may not be running. All of that was settled at the moment a
/// recording finished, invisibly, and the only way to find out what had been chosen was to read
/// the log afterwards — or to notice that a summary never appeared.
///
/// Two things make this worth a screen rather than a line in settings:
///
///   <b>The order is real and it matters.</b> Hosted transcription endpoints are tried in
///   sequence, so the second one is what runs when the first is out of credit. Somebody who has
///   configured three of them has expressed a preference, and preferences that cannot be seen
///   cannot be checked.
///
///   <b>"Configured" and "working" are different questions.</b> A local model server is
///   configured by default and is usually not running. Settings can only answer the first
///   question; this asks the second, out loud, on demand.
/// </summary>
public sealed partial class AiStatusViewModel(Func<AppSettings> settings, HttpClient http) : ObservableObject
{
    /// <summary>Transcription, in the order it will be attempted.</summary>
    public ObservableCollection<AiServiceRow> Transcription { get; } = [];

    /// <summary>Analysis. One entry today; a list because that is the shape it will grow into.</summary>
    public ObservableCollection<AiServiceRow> Analysis { get; } = [];

    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] private DateTimeOffset? _checkedAt;

    /// <summary>One line for the places that have room for one line.</summary>
    public string Summary
    {
        get
        {
            var stt = Transcription.FirstOrDefault(r => r.IsActive)?.Name ?? "yok";
            var llm = Analysis.FirstOrDefault(r => r.IsActive)?.Name ?? "yok";

            return $"Yazıya dökme: {stt} · Çözümleme: {llm}";
        }
    }

    /// <summary>True when analysis is switched off, so its absence is a choice rather than a fault.</summary>
    public bool AnalysisDisabled => !settings().AnalyseAutomatically;

    [RelayCommand]
    public void Refresh()
    {
        var current = settings();

        Transcription.Clear();
        Analysis.Clear();

        BuildTranscription(current);
        BuildAnalysis(current);

        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(AnalysisDisabled));
    }

    private void BuildTranscription(AppSettings current)
    {
        var order = 1;

        // The local engine comes first whenever it is allowed to, because that is the order the
        // orchestrator resolves in: local unless the mode forbids it or the hardware cannot.
        if (current.AsrMode != TranscriptionMode.CloudOnly)
        {
            var model = current.ResolveAsrModel(localTranscriptionUsable: true);

            Transcription.Add(new AiServiceRow(
                order++,
                model.DisplayName,
                $"bu makinede · {current.AsrDevice}",
                isActive: true,
                sendsDataOffMachine: false)
            {
                Reach = ServiceReach.Local,
            });
        }

        // Then the hosted endpoints, in the order they are actually tried.
        foreach (var endpoint in current.UsableSttEndpoints)
        {
            Transcription.Add(new AiServiceRow(
                order,
                endpoint.ResolvedName,
                endpoint.BaseUrl,
                isActive: order == 1,
                sendsDataOffMachine: true));

            order++;
        }

        if (Transcription.Count == 0)
        {
            Transcription.Add(new AiServiceRow(
                1, "Yapılandırılmamış", "Ayarlardan bir yol seç", isActive: false, sendsDataOffMachine: false)
            {
                Reach = ServiceReach.Incomplete,
            });
        }
    }

    private void BuildAnalysis(AppSettings current)
    {
        var provider = current.Provider;

        var reach = !current.LlmReachableInPrinciple
            ? ServiceReach.Incomplete
            : ServiceReach.Unknown;

        Analysis.Add(new AiServiceRow(
            1,
            provider.DisplayName,
            $"{current.ResolvedModelName} · {current.ResolvedBaseUrl}",
            isActive: true,
            provider.SendsDataOffMachine)
        {
            Reach = reach,
        });
    }

    /// <summary>
    /// Asks each service whether it is there.
    ///
    /// On demand rather than continuously. These are network calls against services the user is
    /// paying for or running themselves, and polling them in the background to keep a dot green
    /// would be spending somebody's money to decorate a screen.
    /// </summary>
    [RelayCommand]
    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        if (IsChecking) return;

        IsChecking = true;

        try
        {
            var current = settings();

            foreach (var row in Analysis.Concat(Transcription))
            {
                if (row.Reach is ServiceReach.Local or ServiceReach.Incomplete) continue;

                row.Reach = ServiceReach.Unknown;
            }

            // Analysis first: it is the one that is usually configured and not running, so it is
            // the answer somebody opening this screen is most likely looking for.
            if (current.LlmReachableInPrinciple)
            {
                var client = new OpenAiCompatibleClient(
                    http, current.LlmProvider, current.ResolvedBaseUrl, current.LlmApiKey);

                Analysis[0].Reach = await ReachAsync(
                    () => client.IsAvailableAsync(cancellationToken), cancellationToken);
            }

            var probe = new SttProbe(http);

            foreach (var row in Transcription.Where(r => r.SendsDataOffMachine))
            {
                var endpoint = current.UsableSttEndpoints
                    .FirstOrDefault(e => e.ResolvedName == row.Name);

                if (endpoint is null) continue;

                // Reachable and authorised, not merely answering: an endpoint that responds
                // with 401 will fail every real request, and calling that "bağlandı" would
                // send somebody looking for the fault everywhere except at their key.
                row.Reach = await ReachAsync(
                    async () =>
                    {
                        var result = await probe.TestAsync(endpoint, cancellationToken);
                        return result.Reachable && result.Authorised;
                    },
                    cancellationToken);
            }

            CheckedAt = DateTimeOffset.Now;
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>
    /// One bounded attempt. Unreachable is an answer, not an error.
    ///
    /// The shared client is set to ten minutes because it also uploads hours of audio; a
    /// reachability check inheriting that would leave this screen spinning for the rest of the day
    /// against a service that simply is not there.
    /// </summary>
    private static async Task<ServiceReach> ReachAsync(
        Func<Task<bool>> ask, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));

        try
        {
            return await ask() ? ServiceReach.Reachable : ServiceReach.Unreachable;
        }
        catch (Exception)
        {
            return ServiceReach.Unreachable;
        }
    }
}
