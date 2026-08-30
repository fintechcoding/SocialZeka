using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VoiceTranscript.Core.Domain;
using VoiceTranscript.Core.Storage;

namespace VoiceTranscript.Core.Export;

public sealed record NotionOptions
{
    public required string ApiKey { get; init; }
    public required string DatabaseId { get; init; }

    public string BaseUrl { get; init; } = "https://api.notion.com/v1";

    /// <summary>
    /// Pinned so a change at the other end cannot silently alter what is sent. Notion versions
    /// its API by date and keeps old versions working; following "latest" would mean the payload
    /// shape could change under a running install.
    /// </summary>
    public string ApiVersion { get; init; } = "2022-06-28";
}

/// <summary>Names of the database columns this exporter will fill if they happen to exist.</summary>
public static class NotionFields
{
    public const string Date = "Tarih";
    public const string Duration = "Süre";
    public const string App = "Uygulama";
    public const string Contact = "Kişi";
    public const string FlagCount = "Bayrak";
}

/// <summary>
/// Sends a one-page summary of a call to a Notion database.
///
/// Notion is a cloud service, so this is off unless it is deliberately switched on, and what it
/// sends is deliberately narrow: who, when, how long, the model's short summary, and the ledger
/// lines with their quotes. <b>The full transcript and the audio never leave the machine.</b>
/// A recording of somebody's voice is not the kind of thing to upload as a side effect of a
/// checkbox, and the summary is what makes the archive searchable from a phone anyway.
///
/// Databases are not assumed to have any particular shape. The target database is read first and
/// each field is written only where a column of a compatible type already exists under the
/// expected name; everything else goes into the page body. That means this works against a
/// database the user already had, instead of demanding they build one to a specification.
/// </summary>
public sealed class NotionExporter(Repository repository, NotionOptions options, HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Notion rejects a rich-text run longer than this.</summary>
    private const int MaxTextRun = 2000;

    /// <summary>
    /// Everything this exporter is capable of sending, in the order it appears on the page.
    /// The settings screen shows this list so the answer to "what exactly goes to Notion?" is
    /// something the user can read rather than something they have to trust.
    /// </summary>
    public static IReadOnlyList<string> WhatIsSent =>
    [
        "Kişi adı",
        "Görüşme tarihi ve saati",
        "Süre",
        "Uygulama (WhatsApp / Telegram)",
        "Çözümlemenin ürettiği kısa özet",
        "Defter satırları ve dayandıkları alıntılar",
    ];

    /// <summary>Everything this exporter will never send, whatever the settings say.</summary>
    public static IReadOnlyList<string> WhatIsNeverSent =>
    [
        "Ses kayıtları",
        "Görüşmenin tam metni",
        "Dosya yolları",
    ];

    /// <summary>Creates one Notion page for a call. Returns the page URL.</summary>
    public async Task<string> ExportCallAsync(long callId, CancellationToken ct = default)
    {
        var call = repository.GetCall(callId)
            ?? throw new InvalidOperationException($"Call {callId} not found.");

        // Group calls are recorded as audio only, so there is no summary and no ledger to send.
        // Uploading a bare row saying a group call happened is noise, not an archive.
        if (call.Kind == CallKind.Group)
            throw new InvalidOperationException("Grup aramalarının çözümlemesi olmadığı için Notion'a gönderilmez.");

        var contact = call.ContactId is { } id ? repository.GetContact(id) : null;
        var summary = repository.GetSummary(callId);
        var flags = (contact is null ? [] : repository.GetFlags(contact.Id))
            .Where(f => f.CallId == callId && !f.DismissedByUser)
            .ToList();

        var schema = await ReadDatabaseAsync(ct);

        var page = new JsonObject
        {
            ["parent"] = new JsonObject { ["database_id"] = options.DatabaseId },
            ["properties"] = BuildProperties(schema, call, contact, flags.Count),
            ["children"] = BuildBody(call, summary, flags),
        };

        using var response = await SendAsync(HttpMethod.Post, "pages", page, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Notion sayfayı kabul etmedi ({(int)response.StatusCode}): {Explain(body)}");

        return JsonNode.Parse(body)?["url"]?.GetValue<string>() ?? "";
    }

    /// <summary>
    /// Checks the key and the database without writing anything.
    ///
    /// Worth its own button in the settings screen: the alternative is discovering that the
    /// integration was never shared with the database after a week of calls have quietly failed
    /// to export.
    /// </summary>
    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        var schema = await ReadDatabaseAsync(ct);

        var title = schema.FirstOrDefault(p => p.Value == "title").Key ?? "(yok)";
        var recognised = new[]
            {
                NotionFields.Date, NotionFields.Duration,
                NotionFields.App, NotionFields.Contact, NotionFields.FlagCount,
            }
            .Where(schema.ContainsKey)
            .ToList();

        var found = recognised.Count == 0
            ? "Tanınan sütun yok; her şey sayfa gövdesine yazılacak."
            : $"Kullanılacak sütunlar: {string.Join(", ", recognised)}.";

        return $"Bağlantı çalışıyor. Başlık sütunu: {title}. {found}";
    }

    // ---- Notion plumbing ---------------------------------------------------

    private async Task<Dictionary<string, string>> ReadDatabaseAsync(CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"databases/{options.DatabaseId}", null, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            // The overwhelmingly common cause, and one the error text does not spell out.
            var hint = (int)response.StatusCode == 404
                ? " Veritabanı bulunamadı: Notion'da sayfayı açıp integration'ı bu veritabanına " +
                  "ekledin mi? Paylaşılmayan bir veritabanı, yanlış kimlikle aynı hatayı verir."
                : "";

            throw new InvalidOperationException(
                $"Notion veritabanı okunamadı ({(int)response.StatusCode}): {Explain(body)}{hint}");
        }

        var properties = JsonNode.Parse(body)?["properties"]?.AsObject();
        var schema = new Dictionary<string, string>(StringComparer.Ordinal);

        if (properties is null) return schema;

        foreach (var (name, value) in properties)
        {
            var type = value?["type"]?.GetValue<string>();
            if (type is not null) schema[name] = type;
        }

        return schema;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, $"{options.BaseUrl.TrimEnd('/')}/{path}");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        request.Headers.Add("Notion-Version", options.ApiVersion);

        if (body is not null)
        {
            request.Content = new StringContent(
                body.ToJsonString(Json), Encoding.UTF8, "application/json");
        }

        return await http.SendAsync(request, ct);
    }

    /// <summary>Pulls the human-readable part out of a Notion error body.</summary>
    private static string Explain(string body)
    {
        try
        {
            return JsonNode.Parse(body)?["message"]?.GetValue<string>() ?? Trim(body);
        }
        catch (JsonException)
        {
            return Trim(body);
        }

        static string Trim(string text) => text.Length > 300 ? text[..300] + "…" : text;
    }

    // ---- payload -----------------------------------------------------------

    private static JsonObject BuildProperties(
        IReadOnlyDictionary<string, string> schema, Call call, Contact? contact, int flagCount)
    {
        var properties = new JsonObject();
        var name = contact?.Name ?? "Bilinmeyen kişi";
        var when = call.StartedAt.ToLocalTime();

        // Exactly one title property exists in every Notion database, and it is required.
        // Its name is whatever the user called it, so it is found by type rather than by name.
        var titleProperty = schema.FirstOrDefault(p => p.Value == "title").Key;
        if (titleProperty is not null)
        {
            properties[titleProperty] = new JsonObject
            {
                ["title"] = TextRuns($"{name} — {when:d MMMM yyyy HH:mm}"),
            };
        }

        Put(NotionFields.Date, "date", () => new JsonObject
        {
            ["date"] = new JsonObject { ["start"] = when.ToString("o", CultureInfo.InvariantCulture) },
        });

        Put(NotionFields.Duration, "number", () => new JsonObject
        {
            ["number"] = Math.Round(call.Duration.TotalMinutes, 1),
        });

        Put(NotionFields.App, "select", () => new JsonObject
        {
            ["select"] = new JsonObject { ["name"] = call.App.ToString() },
        });

        Put(NotionFields.Contact, "rich_text", () => new JsonObject
        {
            ["rich_text"] = TextRuns(name),
        });

        Put(NotionFields.FlagCount, "number", () => new JsonObject { ["number"] = flagCount });

        return properties;

        void Put(string field, string expectedType, Func<JsonObject> build)
        {
            // Both checks matter: writing a date into a text column is rejected outright, and a
            // column the user never created must not be invented for them.
            if (schema.TryGetValue(field, out var type) && type == expectedType)
                properties[field] = build();
        }
    }

    private static JsonArray BuildBody(Call call, CallSummary? summary, IReadOnlyList<Flag> flags)
    {
        var blocks = new JsonArray();

        if (summary is not null)
        {
            blocks.Add(Heading("Özet"));
            foreach (var paragraph in Split(summary.Summary)) blocks.Add(Paragraph(paragraph));

            if (!string.IsNullOrWhiteSpace(summary.ActionItems))
            {
                blocks.Add(Heading("Yapılacaklar"));
                foreach (var paragraph in Split(summary.ActionItems!)) blocks.Add(Paragraph(paragraph));
            }
        }
        else
        {
            blocks.Add(Paragraph("Bu görüşme için çözümleme üretilmedi."));
        }

        if (flags.Count > 0)
        {
            blocks.Add(Heading("Defter"));

            foreach (var flag in flags)
            {
                // The quote travels with the claim, always. A ledger line without the words it
                // rests on is an accusation, and this archive is about real people.
                var caveats = new List<string>();
                if (flag.IsHeuristic) caveats.Add("kural tabanlı");
                if (flag.LowConfidence) caveats.Add("ses net değil");

                var suffix = caveats.Count > 0 ? $" ({string.Join(", ", caveats)})" : "";

                blocks.Add(Bullet($"{flag.Summary}{suffix}"));
                blocks.Add(Quote($"“{flag.Quote}” — {Stamp(flag.QuoteStartMs)}"));

                if (!string.IsNullOrWhiteSpace(flag.CounterQuote))
                {
                    blocks.Add(Quote(
                        $"“{flag.CounterQuote}” — daha önce" +
                        (flag.CounterQuoteStartMs is { } ms ? $", {Stamp(ms)}" : "")));
                }
            }
        }

        blocks.Add(Divider());
        blocks.Add(Paragraph(
            $"VoiceTranscript tarafından yazıldı. Ses kaydı ve tam metin bu makinede kaldı; " +
            $"buraya yalnızca özet ve defter gönderildi. Görüşme kimliği: {call.Id}."));

        return blocks;
    }

    private static string Stamp(int milliseconds) =>
        TimeSpan.FromMilliseconds(milliseconds).ToString(@"mm\:ss");

    /// <summary>Splits text into chunks Notion will accept, preferring paragraph boundaries.</summary>
    private static IEnumerable<string> Split(string text)
    {
        foreach (var paragraph in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = paragraph.Trim();
            if (trimmed.Length == 0) continue;

            for (var i = 0; i < trimmed.Length; i += MaxTextRun)
                yield return trimmed[i..Math.Min(i + MaxTextRun, trimmed.Length)];
        }
    }

    private static JsonArray TextRuns(string text)
    {
        var runs = new JsonArray();

        // A single run over the limit is rejected, so long text is split rather than truncated.
        var trimmed = text.Length == 0 ? " " : text;

        for (var i = 0; i < trimmed.Length; i += MaxTextRun)
        {
            runs.Add(new JsonObject
            {
                ["type"] = "text",
                ["text"] = new JsonObject
                {
                    ["content"] = trimmed[i..Math.Min(i + MaxTextRun, trimmed.Length)],
                },
            });
        }

        return runs;
    }

    private static JsonObject Block(string type, JsonObject payload) => new()
    {
        ["object"] = "block",
        ["type"] = type,
        [type] = payload,
    };

    private static JsonObject Heading(string text) =>
        Block("heading_2", new JsonObject { ["rich_text"] = TextRuns(text) });

    private static JsonObject Paragraph(string text) =>
        Block("paragraph", new JsonObject { ["rich_text"] = TextRuns(text) });

    private static JsonObject Bullet(string text) =>
        Block("bulleted_list_item", new JsonObject { ["rich_text"] = TextRuns(text) });

    private static JsonObject Quote(string text) =>
        Block("quote", new JsonObject { ["rich_text"] = TextRuns(text) });

    private static JsonObject Divider() => Block("divider", new JsonObject());
}
