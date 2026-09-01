namespace VoiceTranscript.Core.Domain;

/// <summary>
/// How one tag looks: its icon and colour, the way Outlook gives a category both.
///
/// A definition is appearance only. The tag itself lives in call_tag the moment somebody types
/// it, with or without a row here; a tag with no definition still renders, with a colour hashed
/// from its name and a generic icon. Deleting a definition therefore never deletes a tagging —
/// it only takes the costume away.
/// </summary>
/// <param name="Tag">The spelling shown on screen.</param>
/// <param name="Icon">A WPF-UI SymbolRegular name, e.g. "Flag24". Unknown names fall back safely.</param>
/// <param name="Color">Hex colour like "#E81123".</param>
/// <param name="Position">Order in pickers and the manager list.</param>
public sealed record TagDef(string Tag, string Icon, string Color, int Position = 0);
