using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using VoiceTranscript.App.Services;
using VoiceTranscript.Core.Text;

namespace VoiceTranscript.Tests;

/// <summary>
/// The interface coherence contract — the half that is not markup.
///
/// The user asked for the product to feel like one thing rather than a collection of screens.
/// That is not a thing one can promise in a meeting: it decays the first time somebody adds a
/// screen without reading the other nine. So it is written down as rules, and each rule is a
/// test that goes red when a screen leaves the agreement.
///
/// Two of these are rulers rather than gates. A rule that is born red gets switched off in a
/// week; a number pinned to today's count cannot rise, and every fix lowers it.
/// </summary>
public class SurfaceContractTests
{
    private static readonly string Root = FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VoiceTranscript.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Depo kökü bulunamadı.");
    }

    private static string App(params string[] parts) =>
        Path.Combine([Root, "src", "VoiceTranscript.App", .. parts]);

    private static string Core(params string[] parts) =>
        Path.Combine([Root, "src", "VoiceTranscript.Core", .. parts]);

    private static IEnumerable<string> SourceFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(file => file, StringComparer.Ordinal);

    private static Dictionary<string, string> Dictionary(string code) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Core("Resources", $"strings.{code}.json")))
        ?? throw new InvalidOperationException(code);

    /// <summary>
    /// The source with its comments blanked out, positions preserved.
    ///
    /// Every scan below is looking for things the compiler sees. A comment that quotes the fault
    /// it is warning about — and the ones in this repository do, because that is how they explain
    /// themselves — would otherwise be counted as the fault. Blanking rather than deleting keeps
    /// the character offsets, so a match can still be reported at its real line.
    /// </summary>
    private static string WithoutComments(string source)
    {
        var text = source.ToCharArray();
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') text[i++] = ' ';
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                while (i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/'))
                {
                    if (text[i] != '\n') text[i] = ' ';
                    i++;
                }

                if (i < text.Length) text[i++] = ' ';
                if (i < text.Length) text[i++] = ' ';
            }
            else if (c == '"')
            {
                // Copied over whole: a comment marker inside a string is not a comment.
                var verbatim = i > 0 && text[i - 1] == '@';
                i++;

                while (i < text.Length)
                {
                    if (verbatim)
                    {
                        if (text[i] == '"' && i + 1 < text.Length && text[i + 1] == '"') { i += 2; continue; }
                        if (text[i] == '"') { i++; break; }
                    }
                    else
                    {
                        if (text[i] == '\\') { i += 2; continue; }
                        if (text[i] == '"' || text[i] == '\n') { i++; break; }
                    }

                    i++;
                }
            }
            else
            {
                i++;
            }
        }

        return new string(text);
    }

    /// <summary>Every string literal in a file, with its offset. Verbatim and interpolated included.</summary>
    private static readonly Regex Literal =
        new(@"@""(?:[^""]|"""")*""|""(?:\\.|[^""\\\n])*""", RegexOptions.Compiled);

    /// <summary>A literal written as the key of a dictionary lookup, which is the whole point of the rule.</summary>
    private static readonly Regex DictionaryCall =
        new(@"Localisation\.T\(\s*$", RegexOptions.Compiled);

    private static string Body(string literal) =>
        literal.StartsWith('@') ? literal[2..^1] : literal[1..^1];

    /// <summary>
    /// Whether a literal has any words in it.
    ///
    /// The escapes go first: "\n" is a line break, and counting its "n" as a letter would make
    /// every string.Join separator in the tree look like something somebody has to translate.
    /// </summary>
    private static bool HasWords(string body) =>
        Escape.Replace(body, "").Any(char.IsLetter);

    private static readonly Regex Escape = new(@"\\.", RegexOptions.Compiled);

    private static string Where(string file, string text, int offset) =>
        $"{Path.GetFileName(file)}:{text.Take(offset).Count(c => c == '\n') + 1}";

    // ---- K1: the registry ------------------------------------------------------------------

    /// <summary>
    /// Goes red when a concept the user sees has no row, or has a row that does not say what it
    /// is: which of the three grounds it stands on, what can be done to it, and where it appears.
    ///
    /// The nine are the things a person points at on screen — a promise, a suggestion, a finding,
    /// a to-do, a reminder, a moment, a person, a reading, a circle. An unregistered tenth is not
    /// a missing line in a file; it is a concept whose verbs nobody decided, which is how the
    /// same act ends up on three screens under three names and absent from the fourth.
    /// </summary>
    [Fact]
    public void EveryConceptTheUserSeesHasExactlyOneRow()
    {
        var expected = new[]
        {
            SurfaceRegistry.Promise, SurfaceRegistry.Suggestion, SurfaceRegistry.Finding,
            SurfaceRegistry.Todo, SurfaceRegistry.Reminder, SurfaceRegistry.Moment,
            SurfaceRegistry.Person, SurfaceRegistry.Reading, SurfaceRegistry.Circle,
        };

        Assert.Equal(expected.Length, SurfaceRegistry.All.Count);

        foreach (var concept in expected)
            Assert.Single(SurfaceRegistry.All, row => row.Name == concept);

        foreach (var row in SurfaceRegistry.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(row.Name));
            Assert.True(Enum.IsDefined(row.Ground), $"{row.Name}: zemin yok");
            Assert.True(row.Verbs.Count > 0, $"{row.Name}: fiil kümesi boş");
            Assert.True(row.Surfaces.Count > 0, $"{row.Name}: hiçbir yüzeyde görünmüyor");
            Assert.True(row.Verbs.Distinct().Count() == row.Verbs.Count, $"{row.Name}: fiil iki kez yazılmış");

            // A registry that never records the model's opinion or the user's own hand would be
            // recording only the easy half of the product.
            Assert.True(row.Surfaces.Select(s => (s.Owner, s.Markup)).Distinct().Count() == row.Surfaces.Count,
                $"{row.Name}: aynı yüzey iki kez");
        }

        Assert.Equal(3, SurfaceRegistry.All.Select(r => r.Ground).Distinct().Count());
    }

    // ---- K1's second half: the ground badge has one source ---------------------------------

    /// <summary>
    /// Goes red when a row badge is drawn from a literal somewhere other than
    /// <see cref="RowBadges"/>.
    ///
    /// The same four concepts used to be badged in three different view models, written months
    /// apart, and the clock face already spent on an overdue promise was quietly spent again on
    /// a reminder. A badge is a word in the interface's vocabulary; a vocabulary kept in three
    /// copies is three vocabularies, and the reader is the one who has to reconcile them.
    /// </summary>
    [Fact]
    public void TheRowBadgesComeFromOneSource()
    {
        // The ASCII fallbacks a finding can wear ("?", "!", "•") are punctuation anywhere else
        // in the product, so scanning for them would report every ellipsis in the App layer.
        // What is worth guarding is the pictograms, which have no other use.
        var badges = RowBadges.All
            .Where(badge => badge.Any(c => c > 127))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(badges);

        var offenders = new List<string>();

        foreach (var file in SourceFiles(App()))
        {
            if (Path.GetFileName(file) == "RowBadges.cs") continue;

            var text = WithoutComments(File.ReadAllText(file));

            foreach (Match match in Literal.Matches(text))
            {
                if (badges.Contains(Body(match.Value), StringComparer.Ordinal))
                    offenders.Add($"{Where(file, text, match.Index)} {Body(match.Value)}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Satır rozeti tek kaynaktan gelir: " + string.Join(", ", offenders));
    }

    // ---- K2: completeness ------------------------------------------------------------------

    /// <summary>
    /// Goes red when a surface the registry calls "Tam" has stopped doing one of the verbs, or
    /// when a surface it calls read-only has quietly grown one.
    ///
    /// All or none, on purpose. A screen offering three of four verbs is worse than one offering
    /// none: the missing fourth is invisible, so the user does not learn that the act lives
    /// elsewhere — they learn that it does not exist. This checks both halves, which is what
    /// makes the read-only claim worth writing down at all.
    /// </summary>
    [Fact]
    public void AFullSurfaceDoesEveryVerbAndAReadOnlyOneDoesNone()
    {
        const BindingFlags Anywhere =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
            BindingFlags.Static | BindingFlags.FlattenHierarchy;

        foreach (var row in SurfaceRegistry.All)
        {
            var writing = row.Surfaces
                .SelectMany(s => s.Bindings)
                .Select(b => b.Member)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            foreach (var surface in row.Surfaces)
            {
                var markup = File.ReadAllText(App("Views", surface.Markup));
                var label = $"{row.Name} · {surface.Name}";

                if (surface.Completeness == Completeness.Full)
                {
                    foreach (var verb in row.Verbs)
                    {
                        var binding = surface.Bindings.SingleOrDefault(b => b.Verb == verb);

                        Assert.True(binding is not null, $"{label}: \"{verb}\" fiili bağlanmamış");

                        Assert.True(surface.Owner.GetMember(binding!.Member, Anywhere).Length > 0,
                            $"{label}: {surface.Owner.Name} üzerinde {binding.Member} yok");

                        Assert.True(markup.Contains(binding.Member, StringComparison.Ordinal),
                            $"{label}: {surface.Markup} içinde {binding.Member} bağlı değil");
                    }

                    Assert.True(surface.Bindings.Length == row.Verbs.Count,
                        $"{label}: kayıtta olmayan fiil bağlanmış");
                }
                else
                {
                    Assert.True(surface.Bindings.Length == 0,
                        $"{label}: salt okunur diyor ama fiil sayıyor");

                    var smuggled = writing
                        .Where(member => markup.Contains(member, StringComparison.Ordinal))
                        .ToList();

                    Assert.True(smuggled.Count == 0,
                        $"{label}: salt okunur değil — {string.Join(", ", smuggled)}");
                }
            }
        }
    }

    // ---- K3: one clock ---------------------------------------------------------------------

    /// <summary>
    /// Goes red when a file works out minutes and seconds for itself instead of asking
    /// <see cref="Timestamps"/>.
    ///
    /// This is the rule with a real casualty behind it. Formatting a TimeSpan as "mm\:ss" drops
    /// the hour without saying so, and a sentence spoken at 1:05:00 was printed as 05:00 — an
    /// hour away from where it actually is — under a verbatim quote, in an exported clip, in the
    /// window a user opens to check what somebody said. A timestamp is the address at which the
    /// user goes to hear it for themselves; if the address is wrong the quote is not evidence,
    /// it is a claim with a decoration on it.
    ///
    /// The prompts under Core/Analysis are deliberately out of scope: their stamps are read by a
    /// model against a transcript the same code writes, and changing their shape changes what
    /// the analysis is given.
    /// </summary>
    [Fact]
    public void TheMomentOfAQuoteIsWrittenByOneClock()
    {
        // "ms / 60000" beside "% 60"; a TimeSpan told to print "mm\:ss"; a total-seconds count
        // divided down by hand. All three shapes were in the tree, and two of them were wrong.
        var handRolled = new[]
        {
            new Regex(@"/\s*60000[^;\r\n]*%\s*60", RegexOptions.Compiled),
            new Regex(@"mm\\+:ss", RegexOptions.Compiled),
            new Regex(@"/\s*60:00[^;\r\n]*%\s*60:00", RegexOptions.Compiled),

            // An hours branch that also spells out seconds. "2 sa 30 dk" is a length said the
            // way people say lengths, not a clock, and it is not this rule's business.
            new Regex(@"TotalHours\s*>=\s*1(?:[^;]){0,240}?(?:\bSeconds\b|\\:ss)", RegexOptions.Compiled),
        };

        var offenders = new List<string>();

        var scanned = SourceFiles(App())
            .Concat(SourceFiles(Core("Export")))
            .Where(file => Path.GetFileName(file) != "Timestamps.cs");

        foreach (var file in scanned)
        {
            var text = WithoutComments(File.ReadAllText(file));

            foreach (var pattern in handRolled)
            {
                foreach (Match match in pattern.Matches(text))
                    offenders.Add(Where(file, text, match.Index));
            }
        }

        Assert.True(offenders.Count == 0,
            "Elde hesaplanan saat: " + string.Join(", ", offenders.Distinct()));

        // And the one clock says what it is supposed to say. An hour is where the old spelling
        // went wrong, so an hour is where this is checked.
        Assert.Equal("07:31", Timestamps.Clip(451_000));
        Assert.Equal("1:05:00", Timestamps.Clip(3_900_000));
        Assert.Equal("1:30:00", Timestamps.Clip(5_400_000));
        Assert.Equal("00:00", Timestamps.Clip(-5));
        Assert.Equal("4:12", Timestamps.Length(TimeSpan.FromSeconds(252)));
        Assert.Equal("1:04:12", Timestamps.Length(TimeSpan.FromSeconds(3852)));
    }

    // ---- K4: the user's pen ----------------------------------------------------------------

    /// <summary>
    /// Goes red when a screen reads a promise's raw machine columns instead of the effective
    /// ones, and so shows the user something they have already corrected.
    ///
    /// <c>user_obligation</c> and <c>user_deadline_date</c> exist because the extraction gets
    /// wording and dates wrong often enough that the user has to be able to fix them. A screen
    /// reading <c>Obligation</c> undoes that fix silently: the promise reads correctly on Sözler
    /// and wrongly in the contact window, and the user cannot tell which one the product
    /// believes.
    ///
    /// Three places are exempt and stay exempt. The repository owns the columns. The edit window
    /// shows the machine's wording beside the user's on purpose — that is what it is for. And
    /// everything under Core/Analysis quotes what the MACHINE extracted, because a prompt or a
    /// stored flag summary built from the user's sentence is a copy that goes stale the moment
    /// they edit it again, and re-running the analysis would then compare the model against the
    /// user rather than against itself.
    /// </summary>
    [Fact]
    public void TheUsersOwnWordingIsReadThroughTheEffectiveProperties()
    {
        var exempt = new[]
        {
            Core("Domain", "Analysis.cs"),
            Core("Storage", "Repository.cs"),
            App("Views", "EditPromiseWindow.xaml.cs"),
        };

        var raw = new Regex(
            @"\b\w*(?:[Cc]ommitment|[Pp]romise)\w*\s*[?!]?\s*\.\s*(?:Obligation|DeadlineDate)\b",
            RegexOptions.Compiled);

        var offenders = new List<string>();

        foreach (var file in SourceFiles(App()).Concat(SourceFiles(Core())))
        {
            if (exempt.Contains(file, StringComparer.OrdinalIgnoreCase)) continue;
            if (file.Contains($"{Path.DirectorySeparatorChar}Analysis{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;

            var text = WithoutComments(File.ReadAllText(file));

            foreach (Match match in raw.Matches(text))
                offenders.Add($"{Where(file, text, match.Index)} {match.Value}");
        }

        Assert.True(offenders.Count == 0,
            "Kullanıcının kalemini görmeyen okumalar: " + string.Join(", ", offenders));
    }

    // ---- K9: dictionary verbs --------------------------------------------------------------

    /// <summary>
    /// Goes red when a key's name stops matching the act in its value, or when two keys naming
    /// the same act have drifted apart in either language.
    ///
    /// Both halves are about the same failure. A key called <c>gizle</c> whose value reads
    /// "Reddet" is a rename that was done on screen and not in the file, and the next person to
    /// use that key puts "Reddet" somewhere they meant "hide". Seven keys named
    /// <c>gorusmeyi-ac</c> whose English says "Open call" on five screens and "Open the call" on
    /// two teaches an English reader that the two are different acts.
    ///
    /// Two keys that genuinely mean different things are supposed to have different names, so
    /// the comparison is by the key's TAIL and the Turkish together, not by the Turkish alone:
    /// "Görüşme" the tab title and "görüşme" the counter suffix are one word and two jobs, and
    /// case is left standing so they stay apart.
    ///
    /// Four keys are named here as still wrong and still unfixable from this half of the
    /// package: each needs one token changed inside a <c>{loc:T …}</c> in a .xaml file, and the
    /// markup belongs to the other half. They are listed one by one rather than as a rule with a
    /// hole in it — a fifth turns this red.
    /// </summary>
    [Fact]
    public void KeysThatNameTheSameActCarryTheSameWords()
    {
        // "Reddet" under a key called gizle: the word was renamed on screen when the user asked
        // for the refusal to be called a refusal, and the three keys kept the old name. Fixing
        // them means renaming the key, and the key is written into the markup at
        // CallWindow.xaml:1005, ContactsPage.xaml:681 and OverviewPage.xaml:728.
        string[] pinnedByMarkup = ["callwindow.gizle", "contactspage.gizle", "overviewpage.gizle"];

        // "Açık" is the light theme in one place and an open promise in the other — one Turkish
        // word, two concepts, and no English that serves both. The fix is to say which is which
        // in the key name; settingswindow.acik is written into SettingsWindow.xaml:204.
        const string HomographPinnedByMarkup = "acik";

        var tr = Dictionary("tr");
        var en = Dictionary("en");

        // Turkish folding, so İ/ı never hides a match.
        static string Fold(string value) => value
            .Replace('ı', 'i').Replace('İ', 'i').Replace('ş', 's').Replace('Ş', 's')
            .Replace('ğ', 'g').Replace('Ğ', 'g').Replace('ü', 'u').Replace('Ü', 'u')
            .Replace('ö', 'o').Replace('Ö', 'o').Replace('ç', 'c').Replace('Ç', 'c')
            .ToLowerInvariant();

        // The acts this product performs, and the stem each one keeps once it is conjugated:
        // "başlat" appears in a sentence as "başlar", "reddet" as "reddedildi".
        var verbs = new (string Name, string Stem)[]
        {
            ("sil", "sil"), ("ac", "ac"), ("kapat", "kapa"), ("kaydet", "kayde"),
            ("ekle", "ekle"), ("temizle", "temizle"), ("yenile", "yenile"),
            ("duzenle", "duzenle"), ("ertele", "ertele"), ("reddet", "redd"),
            ("gizle", "gizle"), ("goster", "goster"), ("sec", "sec"), ("ara", "ara"),
            ("kopyala", "kopyala"), ("baslat", "basla"), ("durdur", "durdur"),
            ("birlestir", "birles"), ("tasi", "tasi"), ("dinle", "dinle"),
            ("oynat", "oynat"), ("indir", "indir"), ("vazgec", "vazge"), ("unut", "unut"),
        };

        var contradictions = new List<string>();

        foreach (var (key, value) in tr)
        {
            var tail = key[(key.IndexOf('.') + 1)..];
            var last = tail[(tail.LastIndexOf('-') + 1)..];

            // Only when the key is NAMED after the act, and only for a control's own label. A
            // caption or a tooltip explains rather than commands, and asking a sentence to
            // repeat the verb in its key's name would be asking for worse sentences.
            if (verbs.FirstOrDefault(v => v.Name == last) is not { Name.Length: > 0 } verb) continue;
            if (value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 4) continue;

            if (!Fold(value).Contains(verb.Stem, StringComparison.Ordinal))
                contradictions.Add($"{key} → \"{value}\"");
        }

        Assert.True(
            contradictions.All(line => pinnedByMarkup.Any(key => line.StartsWith(key + " ", StringComparison.Ordinal))),
            "Anahtarın adı değerindeki fiili söylemiyor: " + string.Join(", ", contradictions));

        // The decoration around a word is not the word: a placeholder's "…", a heading's ":",
        // the "▲" a late promise wears. Case IS the word, and stays.
        static string Same(string value) =>
            string.Join(' ', value.Trim(' ', '\t', '…', ':', '·', '▲', '→', '.', ' ').Split(
                (char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        var diverged = tr.Keys
            .GroupBy(key => (Tail: key[(key.IndexOf('.') + 1)..], Turkish: Same(tr[key])))
            .Where(group => group.Count() > 1)
            .Where(group => group.Select(k => Same(en.GetValueOrDefault(k, ""))).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(group => $"{group.Key.Tail} [{group.Key.Turkish}] ["
                             + string.Join(" | ", group.Select(k => en.GetValueOrDefault(k, ""))) + "]")
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            diverged.All(line => line.StartsWith(HomographPinnedByMarkup + " ", StringComparison.Ordinal)),
            "Aynı eylemi adlandıran anahtarlar ayrışmış: " + string.Join("; ", diverged));
    }

    // ---- K10: confirmations speak from the dictionary ---------------------------------------

    /// <summary>
    /// Goes red when a confirmation, a warning or a password prompt says anything the
    /// dictionaries have not been told about — the default button labels included.
    ///
    /// This is the product's least reversible moment. "Bu işlem geri alınamaz. Devam edilsin mi?"
    /// with a button reading "Evet" was, in the English interface, the only thing on screen still
    /// speaking Turkish: the user was being asked to agree to a permanent deletion in a language
    /// they had switched away from. The test is not "does it contain a Turkish letter" — most of
    /// these are pure ASCII ("Sil", "Evet", "Tamam") and that test would have stayed green on its
    /// own examples. It is "does it come from the dictionary", whatever the alphabet.
    /// </summary>
    [Fact]
    public void NoConfirmationSpeaksOutsideTheDictionary()
    {
        var calls = new Regex(
            @"Dialogs\s*\.\s*(?:ConfirmAsync|InfoAsync|AskPasswordAsync)\s*\(",
            RegexOptions.Compiled);

        var keys = Dictionary("tr").Keys.ToHashSet(StringComparer.Ordinal);
        var offenders = new List<string>();

        foreach (var file in SourceFiles(App()))
        {
            var text = WithoutComments(File.ReadAllText(file));

            foreach (Match call in calls.Matches(text))
            {
                var span = Arguments(text, call.Index + call.Length - 1);

                foreach (Match literal in Literal.Matches(text[span]))
                {
                    var body = Body(literal.Value);
                    if (!HasWords(body)) continue;

                    // A key chosen by a condition and handed to the dictionary is still the
                    // dictionary speaking.
                    if (keys.Contains(body)) continue;

                    var before = text[Math.Max(0, span.Start.Value + literal.Index - 40)..(span.Start.Value + literal.Index)];
                    if (DictionaryCall.IsMatch(before)) continue;

                    offenders.Add($"{Where(file, text, span.Start.Value + literal.Index)} \"{body}\"");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Onay penceresinde sözlük dışı metin: " + string.Join(", ", offenders));

        // The dialogs' own defaults. A caller that says nothing about the buttons still puts two
        // words on screen, and those two were the ones nobody thought to translate.
        var dialogs = WithoutComments(File.ReadAllText(App("Services", "Dialogs.cs")));
        var inside = new List<string>();

        foreach (Match literal in Literal.Matches(dialogs))
        {
            var body = Body(literal.Value);
            if (!HasWords(body)) continue;

            // A resource key and an element name are plumbing; neither is ever read by anybody.
            if (body.EndsWith("Brush", StringComparison.Ordinal) || body == "DialogHost") continue;

            var before = dialogs[Math.Max(0, literal.Index - 40)..literal.Index];
            if (DictionaryCall.IsMatch(before)) continue;

            inside.Add($"{Where(App("Services", "Dialogs.cs"), dialogs, literal.Index)} \"{body}\"");
        }

        Assert.True(inside.Count == 0,
            "Diyaloğun kendi metni sözlükten gelmiyor: " + string.Join(", ", inside));
    }

    /// <summary>The span between a call's opening bracket and its match.</summary>
    private static Range Arguments(string text, int openBracket)
    {
        var depth = 0;

        for (var i = openBracket; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0) return (openBracket + 1)..i;
        }

        return (openBracket + 1)..text.Length;
    }

    // ---- K11: the date ruler ---------------------------------------------------------------

    /// <summary>
    /// A ruler, not a gate: the number of dates written by hand in the App layer may fall and
    /// may not rise.
    ///
    /// One conversation row was written four ways on four screens, so a reader comparing two of
    /// them could not tell whether it was the same call. <see cref="Dates"/> now holds the four
    /// shapes the interface needs; the calls that already matched one of them were moved onto it
    /// without a character changing on screen, and the rest are counted here.
    ///
    /// Deliberately a ruler. Converting the remainder MOVES TEXT the user reads — "4 Eylül 2026,
    /// 14:32" becoming "4 Eylül, 14:32" is a decision about a screen, not a refactor — so it is
    /// left to be done screen by screen, with the count as the thing that stops it going
    /// backwards. Red means somebody wrote a new date format instead of naming one.
    /// </summary>
    [Fact]
    public void DatesWrittenByHandOnlyEverGetFewer()
    {
        // Sortable and machine-read: a file name, a log line, a SQL parameter. Never read as a
        // date by a person, so never the ruler's business.
        var machine = new[]
        {
            "yyyy-MM-dd", "yyyy-MM", "yyyyMMdd-HHmmss", "yyyyMMddHHmmssfff", "dd.MM.yyyy HH:mm:ss",
        };

        var written = new List<string>();

        foreach (var file in SourceFiles(App()))
        {
            var text = WithoutComments(File.ReadAllText(file));

            foreach (Match match in HandWrittenDate.Matches(text))
            {
                var format = match.Groups["format"].Value;

                if (!format.Contains("MMM", StringComparison.Ordinal)
                    && !format.Contains("yyyy", StringComparison.Ordinal)
                    && !format.Contains("dddd", StringComparison.Ordinal)) continue;

                if (machine.Contains(format, StringComparer.Ordinal)) continue;

                written.Add($"{Where(file, text, match.Index)} {format}");
            }
        }

        // Measured on 7 September 2026, after the four names were adopted at the 42 call sites
        // whose format already WAS one of them — a rename with nothing moving on screen. It was
        // 72 before that. The 30 that are left would move text on a screen if they were pulled
        // onto the ruler, and each of those is a decision about a screen rather than a refactor.
        const int Pinned = 30;

        Assert.True(written.Count <= Pinned,
            $"Elle yazılan tarih sayısı {Pinned} idi, {written.Count} oldu: "
            + string.Join(", ", written.Take(12)));
    }

    /// <summary>A format string handed to ToString, or written after a colon inside an interpolation.</summary>
    private static readonly Regex HandWrittenDate = new(
        @"ToString\(\s*""(?<format>[^""]*)""|:(?<format>[dMyH][^""}\r\n]*)\}",
        RegexOptions.Compiled);

    // ---- K12: the text ruler ---------------------------------------------------------------

    /// <summary>
    /// A ruler: the number of strings the view models hold in their own hands may fall and may
    /// not rise.
    ///
    /// A view model is where the interface's sentences actually get built, and a sentence built
    /// there never reaches either dictionary — so the English interface says it in Turkish and
    /// nobody finds out, because nothing is missing, something is merely wrong. The criterion is
    /// "does it come from the dictionary", not "does it look Turkish": most of the real ones are
    /// pure ASCII.
    ///
    /// It over-counts on purpose. A scanner cannot tell a caption from a log line or an engine
    /// name, and a ruler that argued each case would need a list of exceptions longer than the
    /// rule. Everything it counts is at least a string somebody could put on screen by accident.
    /// Red means a view model gained one; the way down is a dictionary key.
    /// </summary>
    [Fact]
    public void TheViewModelsHoldEverFewerWordsOfTheirOwn()
    {
        var keys = Dictionary("tr").Keys.ToHashSet(StringComparer.Ordinal);
        var held = new List<string>();

        foreach (var file in SourceFiles(App("ViewModels")))
        {
            var text = WithoutComments(File.ReadAllText(file));

            foreach (Match match in Literal.Matches(text))
            {
                var body = Body(match.Value);

                if (!HasWords(body)) continue;

                // A key carried in a variable is still the dictionary speaking.
                if (keys.Contains(body)) continue;

                // A brush looked up by name is a colour, not a word.
                if (body.EndsWith("Brush", StringComparison.Ordinal)) continue;

                var before = text[Math.Max(0, match.Index - 40)..match.Index];
                if (DictionaryCall.IsMatch(before)) continue;

                held.Add($"{Where(file, text, match.Index)} \"{body}\"");
            }
        }

        // Measured on 7 September 2026. It was 738 before the row badges and the confirmations
        // moved out of the view models.
        const int Pinned = 681;

        Assert.True(held.Count <= Pinned,
            $"Görünüm modellerindeki sözlük dışı dize sayısı {Pinned} idi, {held.Count} oldu: "
            + string.Join(", ", held.Take(12)));
    }
}
