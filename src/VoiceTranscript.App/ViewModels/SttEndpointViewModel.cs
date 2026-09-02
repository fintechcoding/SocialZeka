using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceTranscript.Core.Asr;

namespace VoiceTranscript.App.ViewModels;

/// <summary>
/// One configured transcription service, with the two buttons that make it trustworthy.
///
/// "Sına" and "Krediyi sor" exist because of when the alternative failure arrives. A wrong key, a
/// base address missing its <c>/v1</c>, a model the provider has renamed, an account that ran out
/// of credit overnight — none of these are visible until a conversation has already been recorded
/// and the upload fails. At that point the audio is still on disk, but the user has lost their
/// confidence in the thing, which is harder to get back than a transcript.
/// </summary>
/// <summary>What the card header says about a service, in one word.</summary>
public enum ServiceReadiness
{
    Unknown,
    KeyMissing,
    Testing,
    Ready,
    KeyRejected,
    ModelMissing,
    Unreachable,
}

public sealed partial class SttEndpointViewModel : ObservableObject
{
    private readonly SttProbe _probe;

    public SttEndpointViewModel(SttEndpoint endpoint, SttProbe probe)
    {
        _probe = probe;

        Id = endpoint.Id;
        _kind = endpoint.Kind;
        _name = endpoint.ResolvedName;
        _baseUrl = endpoint.ResolvedBaseUrl;
        _apiKey = endpoint.ApiKey;
        _model = endpoint.ResolvedModel;
        _enabled = endpoint.Enabled;

        // A card built from saved settings has a key already; only a new one does not.
        //
        // The field initialiser below says KeyMissing, and assigning _apiKey directly does not
        // run OnApiKeyChanged, so every configured service opened its card wearing an orange
        // "anahtar eksik" badge until the key was retyped. "Sınanmadı" is the honest word: the
        // key is there and nothing has asked the service about it yet.
        _readiness = string.IsNullOrWhiteSpace(_apiKey)
            ? ServiceReadiness.KeyMissing
            : ServiceReadiness.Unknown;

        Models = [.. endpoint.Provider.Models];
    }

    public string Id { get; }

    public System.Collections.ObjectModel.ObservableCollection<string> Models { get; }

    [ObservableProperty] private string _kind;
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _baseUrl;
    [ObservableProperty] private string _apiKey;
    [ObservableProperty] private string _model;
    [ObservableProperty] private bool _enabled;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _statusIsGood;
    [ObservableProperty] private string? _balance;
    [ObservableProperty] private double? _balanceUsed;
    [ObservableProperty] private bool _balanceIsLow;

    public SttProviderInfo Provider => SttProviderCatalog.Find(Kind);

    public bool SupportsBalance => Provider.Balance != BalanceProbe.None;

    public bool CanUpload => Provider.OpenAiCompatible;

    public string? SignupUrl => Provider.SignupUrl;

    public bool HasSignupUrl => SignupUrl is not null;

    public bool HasBalance => Balance is not null;

    partial void OnBalanceChanged(string? value) => OnPropertyChanged(nameof(HasBalance));

    /// <summary>
    /// Switching provider rewrites the address and the model to that provider's defaults.
    ///
    /// The address must be a real value in the box, not a grey hint. A placeholder reads as an
    /// empty required field: it makes somebody hunt for an API base URL in documentation when the
    /// application already knows it, and half of them will type it slightly wrong.
    /// </summary>
    partial void OnKindChanged(string value)
    {
        var provider = Provider;

        BaseUrl = provider.BaseUrl;
        Model = provider.DefaultModel;
        Name = provider.DisplayName;

        Models.Clear();
        foreach (var model in provider.Models) Models.Add(model);

        Status = null;
        Balance = null;

        OnPropertyChanged(nameof(Provider));
        OnPropertyChanged(nameof(SupportsBalance));
        OnPropertyChanged(nameof(CanUpload));
        OnPropertyChanged(nameof(SignupUrl));
        OnPropertyChanged(nameof(HasSignupUrl));
    }

    public SttEndpoint ToEndpoint() => new()
    {
        Id = Id,
        Kind = Kind,
        Name = Name?.Trim() ?? "",
        BaseUrl = BaseUrl?.Trim() ?? "",
        ApiKey = ApiKey?.Trim() ?? "",
        Model = Model?.Trim() ?? "",
        Enabled = Enabled,
    };

    /// <summary>Puts the address and model back to what this provider ships with.</summary>
    [RelayCommand]
    private void ResetAddress()
    {
        BaseUrl = Provider.BaseUrl;
        Model = Provider.DefaultModel;
    }

    [RelayCommand]
    private async Task TestAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        Status = "Sınanıyor…";
        StatusIsGood = false;
        Readiness = ServiceReadiness.Testing;

        try
        {
            var result = await _probe.TestAsync(ToEndpoint());

            Status = result.Message;
            StatusIsGood = result.IsHealthy && result.ModelAvailable;

            Readiness = string.IsNullOrWhiteSpace(ApiKey) ? ServiceReadiness.KeyMissing
                : !result.Reachable ? ServiceReadiness.Unreachable
                : !result.Authorised ? ServiceReadiness.KeyRejected
                : !result.ModelAvailable ? ServiceReadiness.ModelMissing
                : ServiceReadiness.Ready;

            // Replace the built-in suggestions with what the service actually offers. A model
            // list read from the provider is the difference between choosing and guessing.
            if (result.Models.Count > 0)
            {
                var current = Model;

                Models.Clear();
                foreach (var model in result.Models.OrderBy(m => m, StringComparer.OrdinalIgnoreCase))
                    Models.Add(model);

                Model = current;
            }

            if (result.IsHealthy && SupportsBalance) await ReadBalanceAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The (kind, address, key) the current model list was fetched for.</summary>
    private (string Kind, string BaseUrl, string ApiKey)? _modelsFetchedFor;

    /// <summary>
    /// Whether this service would work if asked right now — one word in the card header.
    /// KeyMissing is the state a new card starts in; the others come from the last test.
    /// </summary>
    [ObservableProperty] private ServiceReadiness _readiness = ServiceReadiness.KeyMissing;

    public string ReadinessText => Readiness switch
    {
        ServiceReadiness.KeyMissing => "anahtar eksik",
        ServiceReadiness.Testing => "sınanıyor…",
        ServiceReadiness.Ready => "hazır",
        ServiceReadiness.KeyRejected => "anahtar reddedildi",
        ServiceReadiness.ModelMissing => "model bulunamadı",
        ServiceReadiness.Unreachable => "ulaşılamıyor",
        _ => "sınanmadı",
    };

    public string ReadinessBrushKey => Readiness switch
    {
        ServiceReadiness.Ready => "SystemFillColorSuccessBrush",
        ServiceReadiness.KeyRejected or ServiceReadiness.Unreachable => "SystemFillColorCriticalBrush",
        ServiceReadiness.ModelMissing or ServiceReadiness.KeyMissing => "SystemFillColorCautionBrush",
        _ => "TextFillColorTertiaryBrush",
    };

    partial void OnReadinessChanged(ServiceReadiness value)
    {
        OnPropertyChanged(nameof(ReadinessText));
        OnPropertyChanged(nameof(ReadinessBrushKey));
    }

    private CancellationTokenSource? _autoTest;

    /// <summary>
    /// Fills the model box from the service when it is opened.
    ///
    /// Fetched once per key rather than on every open: the reply does not change between two
    /// clicks, and a request per click would make the box feel broken on a slow network. A
    /// refused key is said in the status line, as is a service that does not publish a list —
    /// in which case the known models stay and a name can still be typed.
    /// </summary>
    [RelayCommand]
    private async Task RefreshModelsAsync()
    {
        if (IsBusy) return;

        var endpoint = ToEndpoint();
        var key = (endpoint.Kind, endpoint.ResolvedBaseUrl, endpoint.ApiKey);

        if (_modelsFetchedFor == key) return;
        if (string.IsNullOrWhiteSpace(endpoint.ApiKey)) return;

        IsBusy = true;

        try
        {
            var listing = await _probe.ListModelsAsync(endpoint);

            if (listing.Unreachable)
            {
                Status = listing.Message;
                StatusIsGood = false;
                return;
            }

            _modelsFetchedFor = key;

            var current = Model;

            Models.Clear();
            foreach (var model in listing.Models) Models.Add(model);

            Model = current;

            Status = listing.Message;
            StatusIsGood = listing.KeyAccepted;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// A pasted key is tested on its own, a moment after typing stops. Nothing used to happen
    /// until the user found "Bağlantıyı sına"; now the header says "hazır" or why not.
    /// </summary>
    partial void OnApiKeyChanged(string value)
    {
        _modelsFetchedFor = null;

        _autoTest?.Cancel();
        _autoTest = new CancellationTokenSource();
        var token = _autoTest.Token;

        if (string.IsNullOrWhiteSpace(value))
        {
            Readiness = ServiceReadiness.KeyMissing;
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(900, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                if (!token.IsCancellationRequested) await TestAsync();
            });
        }, token);
    }

    [RelayCommand]
    private async Task ReadBalanceAsync()
    {
        var result = await _probe.BalanceAsync(ToEndpoint());

        Balance = result.Message;
        BalanceUsed = result.UsedFraction;
        BalanceIsLow = result.IsLow;
    }
}
