using System.Diagnostics;
using System.IO;
using System.Text;

namespace VoiceTranscript.App.Services;

/// <summary>
/// A log on disk, written so it can be sent to somebody else.
///
/// This project is developed on one machine and used on another. The development machine has no
/// NVIDIA card and no sound hardware at all, so the faults that matter — capture, CUDA, real
/// calls — only ever happen somewhere the developer cannot attach a debugger. Until now the only
/// channel between the two was a screenshot of whatever the interface happened to be showing,
/// which is how a missing cuBLAS library spent a day looking like a broken model download.
///
/// Two consequences shape what this is:
///
/// <b>It is written to be read by a stranger.</b> Each line carries the time, the area and the
/// message, and every run starts with a block saying what the machine is. A log that needs the
/// author present to interpret it is not a log.
///
/// <b>It is written to be shared.</b> That makes what goes in it a privacy decision, not a
/// convenience one. This is an application that records private conversations; a log file the
/// user is invited to send to somebody must never carry a word anybody said, a contact's name, a
/// file path containing one, or an API key. So nothing from a transcript, a contact record or a
/// credential is ever passed in — callers log states, counts, durations and errors. The header
/// says so in the file itself, because the person deciding whether to send it deserves to know
/// what they are sending without having to read all of it.
/// </summary>
public static class AppLog
{
    /// <summary>Days of history kept. Long enough to cover "it broke last week", short enough to stay small.</summary>
    private const int KeepDays = 14;

    private static readonly Lock Gate = new();

    private static string? _directory;
    private static string? _currentPath;
    private static DateOnly _currentDay;

    /// <summary>Where the logs are, once started. Null before that.</summary>
    public static string? Directory => _directory;

    /// <summary>Today's file. Null before <see cref="Start"/>.</summary>
    public static string? CurrentFile => _currentPath;

    /// <summary>
    /// Opens the log and writes the header describing this machine.
    ///
    /// Called before anything else in startup, so that a failure during startup is itself logged
    /// — the failures hardest to report are the ones that happen before the window exists.
    /// </summary>
    public static void Start(string directory, string version)
    {
        lock (Gate)
        {
            _directory = directory;

            try
            {
                System.IO.Directory.CreateDirectory(directory);
                Sweep();
            }
            catch (Exception)
            {
                // A log that cannot be written must never stop the application from running.
                _directory = null;
                return;
            }
        }

        Write("app", "──────────────────────────────────────────────────────────");
        Write("app", $"VoiceTranscript {version}");
        Write("app", $"Windows {Environment.OSVersion.Version} · {Environment.ProcessorCount} çekirdek " +
                     $"· {(Environment.Is64BitProcess ? "x64" : "x86")}");
        Write("app", $".NET {Environment.Version}");
        Write("app",
            "Bu dosya paylaşılmak üzere yazılır: konuşma metni, kişi adı ve API anahtarı " +
            "içermez.");
        Write("app", "──────────────────────────────────────────────────────────");
    }

    /// <summary>
    /// Appends one line.
    ///
    /// Flushed immediately rather than buffered. The line worth having is nearly always the last
    /// one before a crash, and that is exactly the line a buffer loses.
    /// </summary>
    public static void Write(string area, string message)
    {
        if (_directory is null) return;

        var line = $"{DateTimeOffset.Now:HH:mm:ss.fff}  {area,-10}  {Collapse(message)}";

        lock (Gate)
        {
            try
            {
                Roll();
                File.AppendAllText(_currentPath!, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException)
            {
                // Another process holding the file, or a full disk. Losing a log line is not
                // worth interrupting a recording over.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        Debug.WriteLine(line);
    }

    /// <summary>Logs an exception with its type and stack, which is what makes one diagnosable.</summary>
    public static void Error(string area, Exception exception, string? context = null)
    {
        if (context is not null) Write(area, context);

        Write(area, $"HATA {exception.GetType().Name}: {exception.Message}");

        var stack = exception.StackTrace;
        if (!string.IsNullOrWhiteSpace(stack))
            foreach (var frame in stack.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(12))
                Write(area, "    " + frame.Trim());

        if (exception.InnerException is { } inner)
            Error(area, inner, "İç hata:");
    }

    /// <summary>
    /// Everything the user should send when reporting a fault, as one string.
    ///
    /// The last few days rather than only today: a fault noticed on Monday was frequently caused
    /// on Friday, and asking somebody to work out which file to attach is asking them to
    /// diagnose it themselves.
    /// </summary>
    public static string Collect(int days = 3)
    {
        if (_directory is null) return "Günlük yazılmıyor.";

        try
        {
            var cutoff = DateOnly.FromDateTime(DateTime.Now).AddDays(-days + 1);

            var files = System.IO.Directory
                .EnumerateFiles(_directory, "vt-*.log")
                .Where(f => DayOf(f) >= cutoff)
                .OrderBy(f => f)
                .ToList();

            if (files.Count == 0) return "Günlük dosyası yok.";

            var builder = new StringBuilder();

            foreach (var file in files)
            {
                builder.AppendLine($"===== {Path.GetFileName(file)} =====");
                builder.AppendLine(ReadShared(file));
                builder.AppendLine();
            }

            return builder.ToString();
        }
        catch (IOException e)
        {
            return $"Günlük okunamadı: {e.Message}";
        }
    }

    /// <summary>Reads a file the logger itself may still have open.</summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        return reader.ReadToEnd();
    }

    private static DateOnly DayOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);

        return name.Length >= 13 && DateOnly.TryParse(name[3..13], out var day)
            ? day
            : DateOnly.MinValue;
    }

    /// <summary>Moves to a new file when the day changes, so an always-on tray app rolls over.</summary>
    private static void Roll()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_currentPath is not null && today == _currentDay) return;

        _currentDay = today;
        _currentPath = Path.Combine(_directory!, $"vt-{today:yyyy-MM-dd}.log");

        Sweep();
    }

    private static void Sweep()
    {
        try
        {
            var cutoff = DateOnly.FromDateTime(DateTime.Now).AddDays(-KeepDays);

            foreach (var file in System.IO.Directory.EnumerateFiles(_directory!, "vt-*.log"))
            {
                if (DayOf(file) >= cutoff) continue;

                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                }
            }
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Puts a multi-line message on one line.
    ///
    /// A Python traceback logged raw makes every following line ambiguous — a reader cannot tell
    /// a continuation from a new entry — and the timestamps stop lining up, which is most of what
    /// makes a log readable at all.
    /// </summary>
    private static string Collapse(string message) =>
        message.ReplaceLineEndings(" ⏎ ").Trim();
}
