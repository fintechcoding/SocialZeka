using System.Globalization;
using System.Text;
using System.Text.Json;

namespace VoiceTranscript.Core.Import;

/// <summary>Where an imported message came from.</summary>
public enum MessageSource
{
    Telegram = 0,
    WhatsApp = 1,
}

/// <summary>One written message, reduced to what this application needs.</summary>
public sealed record ImportedMessage
{
    public required string ExternalId { get; init; }
    public required DateTimeOffset SentAt { get; init; }

    /// <summary>True when the user wrote it. A fact from the export, not a guess.</summary>
    public required bool IsMe { get; init; }

    public required string Text { get; init; }

    /// <summary>The message this one answered, when it answered one.</summary>
    public string? ReplyTo { get; init; }

    /// <summary>
    /// When the message was edited.
    ///
    /// Kept because it is exactly the sort of thing this product exists to notice: a figure that
    /// was written and then quietly changed reads very differently from one that was written once.
    /// </summary>
    public DateTimeOffset? EditedAt { get; init; }

    /// <summary>A photo or file. Carries no words but still marks that somebody wrote.</summary>
    public bool HasAttachment { get; init; }
}

/// <summary>One conversation found in an export.</summary>
public sealed record ImportedConversation
{
    public required string Name { get; init; }
    public required string ChatId { get; init; }
    public required IReadOnlyList<ImportedMessage> Messages { get; init; }

    public DateTimeOffset? FirstAt => Messages.Count > 0 ? Messages[0].SentAt : null;
    public DateTimeOffset? LastAt => Messages.Count > 0 ? Messages[^1].SentAt : null;

    public int FromThem => Messages.Count(m => !m.IsMe);
    public int FromMe => Messages.Count(m => m.IsMe);
}

/// <summary>What an export turned out to contain.</summary>
public sealed record TelegramExportSummary
{
    public required IReadOnlyList<ImportedConversation> Conversations { get; init; }

    /// <summary>Conversations found but deliberately not offered. Named, so the omission is visible.</summary>
    public required IReadOnlyList<string> Skipped { get; init; }

    public int TotalMessages => Conversations.Sum(c => c.Messages.Count);
}

/// <summary>
/// Reads the file Telegram produces from Settings, Advanced, Export Telegram data.
///
/// This route rather than the application's local storage, and the reason is worth stating
/// plainly. Telegram Desktop keeps its data in an encrypted <c>tdata</c> folder. Opening it means
/// defeating that encryption, which breaks on every update, sits against the terms of service,
/// and is behaviourally indistinguishable from spyware — three good reasons on their own, and
/// unnecessary besides, because Telegram publishes a complete export of the user's own data in
/// JSON. The supported door is open, so it is the one used.
///
/// Only one-to-one conversations are imported. Group chats are skipped for the same reason group
/// calls are not transcribed: a claim needs an author, and in a group the interesting question
/// stops being "what did they say" and becomes "which of the eleven of them".
/// </summary>
public static class TelegramExport
{
    /// <summary>Chat types that are one conversation with one person.</summary>
    private static readonly HashSet<string> OneToOne =
        new(StringComparer.Ordinal) { "personal_chat" };

    /// <summary>
    /// Reads an export.
    ///
    /// Streams the file rather than loading it. An export of a few years of messages runs to
    /// hundreds of megabytes of JSON, and reading that into a string first would take several
    /// times its size in memory on a laptop that is also holding a model.
    /// </summary>
    public static async Task<TelegramExportSummary> ReadAsync(
        string path,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Dışa aktarma dosyası bulunamadı.", path);

        progress?.Report("Dosya okunuyor…");

        await using var stream = File.OpenRead(path);

        using var document = await JsonDocument.ParseAsync(
            stream,
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip },
            ct);

        var root = document.RootElement;

        // The whole-account export nests the chats; a single-chat export is the chat itself.
        var chats = root.TryGetProperty("chats", out var wrapper) && wrapper.TryGetProperty("list", out var list)
            ? list.EnumerateArray().ToList()
            : root.TryGetProperty("messages", out _) ? [root] : [];

        if (chats.Count == 0)
        {
            throw new InvalidDataException(
                "Bu dosya bir Telegram dışa aktarması gibi görünmüyor. " +
                "Telegram Desktop'ta Ayarlar → Gelişmiş → Telegram verilerini dışa aktar ile, " +
                "biçim olarak JSON seçerek oluşturulan result.json dosyasını seç.");
        }

        var conversations = new List<ImportedConversation>();
        var skipped = new List<string>();

        foreach (var chat in chats)
        {
            ct.ThrowIfCancellationRequested();

            var type = Text(chat, "type") ?? "";
            var name = Text(chat, "name");

            if (!OneToOne.Contains(type))
            {
                // Named rather than silently dropped: somebody who exported everything should be
                // able to see why their group conversations are not here.
                if (name is { Length: > 0 }) skipped.Add($"{name} ({Describe(type)})");
                continue;
            }

            if (string.IsNullOrWhiteSpace(name)) continue;

            var messages = ReadMessages(chat);
            if (messages.Count == 0) continue;

            conversations.Add(new ImportedConversation
            {
                Name = name.Trim(),
                ChatId = Text(chat, "id") ?? Number(chat, "id") ?? name.Trim(),
                Messages = messages,
            });

            progress?.Report($"{conversations.Count} kişi okundu…");
        }

        return new TelegramExportSummary
        {
            Conversations = [.. conversations.OrderByDescending(c => c.Messages.Count)],
            Skipped = skipped,
        };
    }

    private static List<ImportedMessage> ReadMessages(JsonElement chat)
    {
        if (!chat.TryGetProperty("messages", out var array) || array.ValueKind != JsonValueKind.Array)
            return [];

        var messages = new List<ImportedMessage>();

        foreach (var item in array.EnumerateArray())
        {
            // Service entries record that somebody joined or pinned something. They are not
            // anybody saying anything, and treating them as text would put phrases nobody wrote
            // into a ledger that quotes people.
            if (Text(item, "type") != "message") continue;

            var sentAt = ParseDate(item, "date", "date_unixtime");
            if (sentAt is null) continue;

            var text = PlainText(item);
            var attachment = HasAttachment(item);

            // A message with neither words nor a file is a shape this application has no use for.
            if (text.Length == 0 && !attachment) continue;

            messages.Add(new ImportedMessage
            {
                ExternalId = Number(item, "id") ?? Text(item, "id") ?? sentAt.Value.ToUnixTimeMilliseconds().ToString(),
                SentAt = sentAt.Value,
                IsMe = IsFromMe(item, chat),
                Text = text,
                ReplyTo = Number(item, "reply_to_message_id"),
                EditedAt = ParseDate(item, "edited", "edited_unixtime"),
                HasAttachment = attachment,
            });
        }

        return [.. messages.OrderBy(m => m.SentAt)];
    }

    /// <summary>
    /// Whether the user wrote this message.
    ///
    /// Decided by comparing the sender against the chat's own identity, which is the one thing
    /// an export states unambiguously: in a one-to-one chat every message is either from the
    /// person the chat is with, or from the account that produced the export. Matching on the
    /// display name instead would break the moment two contacts share a first name.
    /// </summary>
    private static bool IsFromMe(JsonElement message, JsonElement chat)
    {
        var fromId = Text(message, "from_id");
        var chatId = Text(chat, "id") ?? Number(chat, "id");

        if (fromId is null || chatId is null)
        {
            // Older exports omit from_id on outgoing messages entirely, which is itself the
            // signal: an incoming message always names its sender.
            return Text(message, "from") is null;
        }

        // Telegram writes the chat identity as a bare number and the sender as "user<number>".
        var senderNumber = fromId.StartsWith("user", StringComparison.Ordinal) ? fromId[4..] : fromId;

        return senderNumber != chatId.TrimStart('-');
    }

    /// <summary>
    /// Flattens the text of a message.
    ///
    /// The <c>text</c> field is a string for a plain message and an array of mixed strings and
    /// objects the moment it contains a link, a mention or any formatting — so reading it as a
    /// string silently loses every message anybody bothered to format. The newer
    /// <c>text_entities</c> field is the same content in one consistent shape and is preferred
    /// where the export carries it.
    /// </summary>
    internal static string PlainText(JsonElement message)
    {
        if (message.TryGetProperty("text_entities", out var entities) && entities.ValueKind == JsonValueKind.Array)
        {
            var builder = new StringBuilder();

            foreach (var entity in entities.EnumerateArray())
                if (Text(entity, "text") is { } part) builder.Append(part);

            var joined = builder.ToString().Trim();
            if (joined.Length > 0) return joined;
        }

        if (!message.TryGetProperty("text", out var text)) return "";

        return text.ValueKind switch
        {
            JsonValueKind.String => text.GetString()?.Trim() ?? "",
            JsonValueKind.Array => Flatten(text),
            _ => "",
        };
    }

    private static string Flatten(JsonElement array)
    {
        var builder = new StringBuilder();

        foreach (var part in array.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String) builder.Append(part.GetString());
            else if (Text(part, "text") is { } inner) builder.Append(inner);
        }

        return builder.ToString().Trim();
    }

    private static bool HasAttachment(JsonElement message) =>
        message.TryGetProperty("photo", out _)
        || message.TryGetProperty("file", out _)
        || message.TryGetProperty("media_type", out _)
        || message.TryGetProperty("sticker_emoji", out _);

    /// <summary>
    /// Reads a timestamp, preferring the Unix field.
    ///
    /// The human-readable one has no time zone at all — it is local time on whatever machine
    /// produced the export — so trusting it would shift every message by the offset between that
    /// machine and this one. The Unix field is unambiguous.
    /// </summary>
    private static DateTimeOffset? ParseDate(JsonElement element, string readable, string unix)
    {
        if (Number(element, unix) is { } seconds && long.TryParse(seconds, out var epoch))
            return DateTimeOffset.FromUnixTimeSeconds(epoch);

        if (Text(element, readable) is { } value
            && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return new DateTimeOffset(parsed, TimeZoneInfo.Local.GetUtcOffset(parsed));
        }

        return null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? Number(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => value.GetString(),
            _ => null,
        };
    }

    private static string Describe(string type) => type switch
    {
        "private_group" or "private_supergroup" or "public_supergroup" => "grup",
        "saved_messages" => "kaydedilen mesajlar",
        "bot_chat" => "bot",
        "channel" or "public_channel" or "private_channel" => "kanal",
        _ => type,
    };
}
