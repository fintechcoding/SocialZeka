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

        try
        {
            var result = await _probe.TestAsync(ToEndpoint());

            Status = result.Message;
            StatusIsGood = result.IsHealthy && result.ModelAvailable;

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

    [RelayCommand]
    private async Task ReadBalanceAsync()
    {
        var result = await _probe.BalanceAsync(ToEndpoint());

        Balance = result.Message;
        BalanceUsed = result.UsedFraction;
        BalanceIsLow = result.IsLow;
    }
}
