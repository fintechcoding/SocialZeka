using VoiceTranscript.App.ViewModels;
using VoiceTranscript.Core.Domain;

namespace VoiceTranscript.Tests;

/// <summary>
/// The archive page narrows by whatever the user knows about the call they want. Each filter
/// is a fact about the call; a filter that matched the wrong rows would hide the call they
/// were looking for behind a confident "Görüşme yok".
/// </summary>
public sealed class CallsFilterTests
{
    private static RecentCall Row(long id, long? contactId, string name, DateTimeOffset at, CallApp app, ProcessingState state, params string[] tags) =>
        new(new Call { Id = id, ContactId = contactId, App = app, StartedAt = at, State = state, Duration = TimeSpan.FromMinutes(3) }, name, tags);

    private static readonly DateTimeOffset Now = DateTimeOffset.Now;

    private static IReadOnlyList<RecentCall> Sample() =>
    [
        Row(1, 10, "Uliana", Now.AddHours(-1), CallApp.WhatsApp, ProcessingState.Analysed, "önemli"),
        Row(2, 11, "Gürhan Abi", Now.AddDays(-1), CallApp.Telegram, ProcessingState.Failed),
        Row(3, null, "İsimsiz", Now.AddDays(-3), CallApp.WhatsApp, ProcessingState.Transcribed),
        Row(4, 10, "Uliana", Now.AddDays(-40), CallApp.WhatsApp, ProcessingState.Queued, "tehdit"),
    ];

    private static IReadOnlyList<RecentCall> Apply(long? contact = null, SearchPeriod period = SearchPeriod.Anytime,
        string app = CallsViewModel.Any, string state = CallsViewModel.Any, string tag = CallsViewModel.Any, string query = "")
        => CallsViewModel.Filter(Sample(), contact, period, app, state, tag, query);

    [Fact]
    public void NoFilterKeepsEverything() => Assert.Equal(4, Apply().Count);

    [Fact]
    public void ByPersonKeepsOnlyTheirCalls()
    {
        var rows = Apply(contact: 10);

        Assert.Equal([1L, 4L], rows.Select(r => r.Call.Id).Order());
    }

    [Fact]
    public void ByPeriodUsesTheCallsOwnClock()
    {
        Assert.Equal([1L], Apply(period: SearchPeriod.Today).Select(r => r.Call.Id));
        Assert.DoesNotContain(Apply(period: SearchPeriod.LastMonth), r => r.Call.Id == 4);
    }

    [Fact]
    public void ByAppAndByStateAndByTag()
    {
        Assert.Equal([2L], Apply(app: "Telegram").Select(r => r.Call.Id));
        Assert.Equal([2L], Apply(state: CallsViewModel.StateFailed).Select(r => r.Call.Id));
        Assert.Equal([3L], Apply(state: CallsViewModel.StateUnnamed).Select(r => r.Call.Id));
        Assert.Equal([4L], Apply(state: CallsViewModel.StateBusy).Select(r => r.Call.Id));
        Assert.Equal([4L], Apply(tag: "tehdit").Select(r => r.Call.Id));
    }

    [Fact]
    public void TheNameBoxIgnoresTurkishCaseAndDiacritics()
    {
        Assert.Equal([2L], Apply(query: "gurhan").Select(r => r.Call.Id));
        Assert.Equal([2L], Apply(query: "GÜRHAN").Select(r => r.Call.Id));
    }

    [Fact]
    public void FiltersCombine()
    {
        var rows = Apply(contact: 10, app: "WhatsApp", state: CallsViewModel.StateDone);

        Assert.Equal([1L], rows.Select(r => r.Call.Id));
    }
}
