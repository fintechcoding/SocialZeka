using System.Globalization;
using System.Text;

namespace VoiceTranscript.Core.Text;

/// <summary>
/// Turkish-aware text handling.
///
/// Two separate problems live here and they must not be confused:
///
/// 1. DISPLAY casing. Turkish has two dotted/dotless i pairs (i/İ and ı/I). Invariant casing
///    turns "İstanbul" into "İSTANBUL" correctly but turns "IŞIK" into "ışık" wrongly, and
///    lowercasing "I" with invariant rules yields "i" instead of "ı". Contact names must use
///    the tr-TR culture so they are shown the way a Turkish speaker writes them.
///
/// 2. SEARCH matching. SQLite FTS5 unicode61 applies simple Unicode case folding, which is
///    wrong for Turkish: a search for "ışık" does not match "IŞIK". Worse, it fails silently —
///    zero rows come back and it looks like the data is missing. The fix is to index and query
///    a folded form where every Turkish-specific letter collapses onto its ASCII base, so all
///    spellings of a word meet in the same bucket.
/// </summary>
public static class TurkishText
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>Lowercase using Turkish rules. "IŞIK" becomes "ışık", "İSTANBUL" becomes "istanbul".</summary>
    public static string ToLowerTr(string value) => value.ToLower(Turkish);

    /// <summary>Uppercase using Turkish rules. "ışık" becomes "IŞIK", "istanbul" becomes "İSTANBUL".</summary>
    public static string ToUpperTr(string value) => value.ToUpper(Turkish);

    /// <summary>
    /// Removes bidirectional and other invisible formatting controls, then applies NFKC.
    ///
    /// Needed because window titles read from Telegram start with U+200E LEFT-TO-RIGHT MARK and
    /// may contain mathematical-alphanumeric styled letters that users set as display names.
    /// Storing those raw would produce contact keys that never match and filenames that break.
    /// </summary>
    public static string StripFormatting(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            // Bidi controls and zero-width joiners carry no meaning for us.
            if (ch is '​' or '‌' or '‍' or '‎' or '‏' or '﻿') continue;
            if (ch is >= '‪' and <= '‮') continue; // embedding / override
            if (ch is >= '⁦' and <= '⁩') continue; // isolates
            if (char.IsControl(ch) && ch is not '\t' && ch is not '\n') continue;

            sb.Append(ch);
        }

        // NFKC folds styled variants (for example U+1D4E2 SCRIPT SMALL S) onto plain letters.
        return sb.ToString().Normalize(NormalizationForm.FormKC).Trim();
    }

    /// <summary>
    /// Folds text into the form stored in the FTS index and used for queries.
    ///
    /// Every Turkish-specific letter collapses onto its ASCII base, so "IŞIK", "ışık", "Işık"
    /// and "isik" all normalise to "isik" and therefore find each other. Combining marks are
    /// dropped so that decomposed input matches precomposed input.
    ///
    /// The deliberate cost is that a few distinct words merge (for example "saç" and "sac").
    /// For free-text recall over speech transcripts that trade is worth it: recall matters far
    /// more than precision, and Whisper output is not reliably accented anyway.
    /// </summary>
    public static string NormalizeForSearch(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var cleaned = StripFormatting(value);
        if (cleaned.Length == 0) return string.Empty;

        // Fold the Turkish letters BEFORE case mapping, so no culture rule can interfere.
        var sb = new StringBuilder(cleaned.Length);
        foreach (var ch in cleaned)
        {
            sb.Append(ch switch
            {
                'İ' or 'I' or 'ı' or 'i' or 'Î' or 'î' => 'i',
                'Ğ' or 'ğ' => 'g',
                'Ş' or 'ş' => 's',
                'Ç' or 'ç' => 'c',
                'Ö' or 'ö' => 'o',
                'Ü' or 'ü' => 'u',
                'Â' or 'â' => 'a',
                'Û' or 'û' => 'u',
                _ => ch,
            });
        }

        // Strip any remaining combining marks (decomposed input, foreign names).
        var decomposed = sb.ToString().Normalize(NormalizationForm.FormD);
        var stripped = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            stripped.Append(ch);
        }

        return stripped.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    /// <summary>
    /// Builds an FTS5 MATCH expression from what the user typed.
    ///
    /// Every token is normalised the same way the index was, and gets a trailing prefix
    /// operator. The prefix is not a nicety: Turkish is agglutinative, so a search for "kitap"
    /// must also reach "kitabı", "kitaptan" and "kitabımı". Full stemming is out of scope, and
    /// prefix matching recovers most of its value for a fraction of the complexity.
    /// </summary>
    public static string ToMatchQuery(string? userInput)
    {
        if (string.IsNullOrWhiteSpace(userInput)) return string.Empty;

        var tokens = NormalizeForSearch(userInput)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 0)
            .Select(Sanitise)
            .Where(t => t.Length > 0)
            .Select(t => t + "*");

        return string.Join(" AND ", tokens);

        // FTS5 treats a bare double quote as a string delimiter and punctuation as operators;
        // keeping only letters and digits removes any chance of a malformed MATCH expression.
        static string Sanitise(string token)
        {
            var sb = new StringBuilder(token.Length);
            foreach (var ch in token)
                if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            return sb.ToString();
        }
    }
}
