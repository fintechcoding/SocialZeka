using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using VoiceTranscript.Core.Llm;

namespace VoiceTranscript.App.Views;

/// <summary>
/// Picks a model from whatever the provider actually offers today.
///
/// The alternative it replaces was a free-text box with five suggestions underneath, written once
/// and out of date within weeks. That arrangement fails quietly and expensively: a provider handed
/// an identifier it does not recognise answers with a 400 whose message frequently does not
/// mention the model, so a typo presents as "analysis stopped working" and stays that way until
/// somebody thinks to re-read a string they believe they typed correctly.
///
/// Asking the provider removes the guess. The search box is not a refinement — OpenRouter alone
/// lists several hundred models, and a list that long without a filter is a worse interface than
/// the text box was.
/// </summary>
public partial class ModelPickerWindow
{
    private readonly HttpClient _http;
    private readonly LlmProviderKind _kind;
    private readonly string _baseUrl;
    private readonly string? _apiKey;

    private IReadOnlyList<RemoteModel> _all = [];
    private CancellationTokenSource? _inFlight;

    public ModelPickerWindow(
        HttpClient http,
        LlmProviderKind kind,
        string providerName,
        string baseUrl,
        string? apiKey,
        string? currentModel)
    {
        InitializeComponent();

        _http = http;
        _kind = kind;
        _baseUrl = baseUrl;
        _apiKey = apiKey;

        ChosenModel = currentModel;
        ProviderLine.Text = providerName;

        Loaded += async (_, _) =>
        {
            SearchBox.Focus();
            await LoadAsync();
        };

        // The window owns the request. Closing it while a fetch is outstanding must not leave a
        // continuation writing into controls that are on their way down.
        Closed += (_, _) =>
        {
            _inFlight?.Cancel();
            _inFlight?.Dispose();
        };
    }

    /// <summary>The identifier chosen, once the window closes with a result.</summary>
    public string? ChosenModel { get; private set; }

    private async Task LoadAsync()
    {
        _inFlight?.Cancel();
        _inFlight?.Dispose();
        _inFlight = new CancellationTokenSource();

        var token = _inFlight.Token;

        Busy.Visibility = Visibility.Visible;
        Problem.Visibility = Visibility.Collapsed;
        ModelList.Visibility = Visibility.Collapsed;
        CountLine.Text = "";

        try
        {
            var fetched = await ModelDirectory.FetchAsync(_http, _kind, _baseUrl, _apiKey, token);

            // Winnowed, then ranked.
            //
            // OpenAI returns 126 entries on an ordinary account and most are not choices — dated
            // duplicates of models already listed, speech synthesisers, embedding models. Handed
            // that list whole, somebody looking for an analysis model is not being offered a
            // choice, they are being handed a haystack.
            var winnowed = ModelRecommendations.Winnow(fetched, forTranscription: false);
            var (recommended, others) = ModelRecommendations.Split(winnowed, _kind);

            _all = [.. recommended, .. others];

            if (token.IsCancellationRequested) return;

            Apply();

            ModelList.Visibility = Visibility.Visible;

            // Restore the model already configured, so the dialog opens on the current answer
            // rather than on whatever happens to be first.
            if (ChosenModel is { Length: > 0 } current)
            {
                var match = _all.FirstOrDefault(m =>
                    string.Equals(m.Id, current, StringComparison.OrdinalIgnoreCase));

                if (match is not null)
                {
                    ModelList.SelectedItem = ModelList.Items
                        .Cast<RemoteModel>()
                        .FirstOrDefault(m => m.Id == match.Id);

                    ModelList.ScrollIntoView(ModelList.SelectedItem);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The window closed, or a refresh superseded this one. Nothing to report either way.
        }
        catch (LlmException e)
        {
            Fail(e.Message);
        }
        catch (Exception e)
        {
            Fail($"Model listesi alınamadı: {e.Message}");
        }
        finally
        {
            if (!token.IsCancellationRequested) Busy.Visibility = Visibility.Collapsed;
        }
    }

    private void Fail(string message)
    {
        Problem.Text = message + Environment.NewLine + Environment.NewLine
            + "Model adını elle de yazabilirsin — bu pencereyi kapat ve kutuya doğrudan gir.";

        Problem.Visibility = Visibility.Visible;
        ModelList.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Filters the list against what has been typed.
    ///
    /// Every word must match somewhere, in any order, so "haiku ucuz" and "ucuz haiku" find the
    /// same thing. Matching the whole phrase as one string would fail for both, which is how a
    /// search box teaches people it does not work.
    /// </summary>
    private void Apply()
    {
        var query = SearchBox.Text?.Trim().ToLowerInvariant() ?? "";

        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var shown = words.Length == 0
            ? _all
            : [.. _all.Where(m => words.All(w => m.Haystack.Contains(w, StringComparison.Ordinal)))];

        ModelList.ItemsSource = shown;

        CountLine.Text = shown.Count == _all.Count
            ? $"{_all.Count} model"
            : $"{_all.Count} modelden {shown.Count} tanesi";

        if (shown.Count > 0 && ModelList.SelectedItem is null) ModelList.SelectedIndex = 0;
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_all.Count > 0) Apply();
    }

    /// <summary>Down from the search box moves into the list, so filtering and picking are one gesture.</summary>
    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Down or Key.Up)) return;
        if (ModelList.Items.Count == 0) return;

        ModelList.Focus();
        e.Handled = true;
    }

    private void ModelList_DoubleClick(object sender, MouseButtonEventArgs e) => Choose_Click(sender, e);

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private void Choose_Click(object sender, RoutedEventArgs e)
    {
        if (ModelList.SelectedItem is not RemoteModel model) return;

        ChosenModel = model.Id;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
