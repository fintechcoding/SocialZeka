using System.Net.Http;
using VoiceTranscript.Core.Configuration;

namespace VoiceTranscript.App.Services;

/// <summary>
/// What a screen needs in order to spend money on a model, in one parameter.
///
/// The three pieces always travel together — the settings say which provider and which model, the
/// client is the one shared <see cref="HttpClient"/>, and Save exists because two of these
/// features can switch themselves off after measuring badly. Passing them one at a time down
/// three constructors made every call site a place to forget one.
///
/// Optional wherever it appears: a screen built without it (a smoke test, a preview) shows the
/// feature as unavailable rather than reaching for <c>Application.Current</c> to find a way.
/// </summary>
/// <param name="Settings">Read fresh each time — the user may have changed them since.</param>
/// <param name="Save">Persists an amended copy AND makes it the running application's.</param>
public sealed record ModelAccess(
    Func<AppSettings> Settings,
    Action<AppSettings> Save,
    HttpClient Http);
