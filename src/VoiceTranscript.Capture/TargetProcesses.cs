using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using VoiceTranscript.Core.Domain;

namespace VoiceTranscript.Capture;

/// <summary>
/// Works out which running processes belong to the applications being watched.
///
/// This is deliberately not a list of executable names. WhatsApp Desktop is a packaged Store app
/// whose root executable was renamed from WhatsApp.exe to WhatsApp.Root.exe, and whose install
/// path contains the version number, so it changes with every update. Matching on package
/// identity survives both. The executable names stay in the list only as a fallback in case
/// the packaging changes back.
///
/// It also matters that child processes are included. WhatsApp hosts its chat UI in WebView2,
/// and although its calls are native — WhatsAppNative.Voip.dll runs in the root process — a
/// future build could move audio into a child. Walking the tree costs nothing and removes the
/// assumption.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TargetProcesses
{
    /// <summary>
    /// Package family names of the watched applications, when they are Store apps.
    ///
    /// Matched before executable names because package identity survives both a rename and the
    /// versioned install path — WhatsApp Desktop's root executable was renamed from WhatsApp.exe
    /// to WhatsApp.Root.exe, and its folder carries the version number, so it changes on every
    /// update.
    /// </summary>
    private static readonly (string Family, CallApp App)[] PackageFamilies =
    [
        ("5319275A.WhatsAppDesktop_cv1g1gvanyjgm", CallApp.WhatsApp),
        ("TelegramMessengerLLP.TelegramDesktop_t4vj0pshhgkwm", CallApp.Telegram),

        // "Telegram" from the Microsoft Store is frequently Unigram — a different code base with
        // the same calls, published under this family. Its executable has carried both names.
        ("38833FF26BA1D.UnigramPreview_g9c9v27vpyspw", CallApp.Telegram),
    ];

    private static readonly string[] WhatsAppExecutables = ["WhatsApp.Root.exe", "WhatsApp.exe"];

    /// <summary>
    /// Telegram Desktop and the forks people actually run.
    ///
    /// The forks matter because somebody using one is still making Telegram calls, and a watcher
    /// that only knows the official binary records nothing for them without ever saying so.
    /// </summary>
    private static readonly string[] TelegramExecutables =
        ["Telegram.exe", "AyuGram.exe", "64Gram.exe", "Kotatogram.exe", "Unigram.exe"];

    /// <summary>
    /// Signal Desktop.
    ///
    /// An Electron application installed per-user under
    /// <c>%LOCALAPPDATA%\Programs\signal-desktop</c>. Its calls run in the main process, but the
    /// renderer children are picked up anyway by walking the process tree, which costs nothing
    /// and removes an assumption that a future build could invalidate.
    /// </summary>
    private static readonly string[] SignalExecutables = ["Signal.exe", "Signal Beta.exe"];

    private readonly TimeSpan _cacheFor;
    private DateTimeOffset _refreshedAt = DateTimeOffset.MinValue;
    private Dictionary<int, CallApp> _cache = [];

    public TargetProcesses(TimeSpan? cacheFor = null) => _cacheFor = cacheFor ?? TimeSpan.FromSeconds(5);

    /// <summary>
    /// Process ids belonging to a watched application, mapped to which one.
    ///
    /// Cached briefly: the detector polls once a second, and walking every process tree that
    /// often is wasteful when the answer changes only when an app starts or stops.
    /// </summary>
    public IReadOnlyDictionary<int, CallApp> Resolve(DateTimeOffset now)
    {
        if (now - _refreshedAt < _cacheFor) return _cache;

        var result = new Dictionary<int, CallApp>();

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var app = Identify(process);
                if (app == CallApp.Unknown) continue;

                result[process.Id] = app;
                foreach (var childId in DescendantsOf(process.Id)) result[childId] = app;
            }
            catch (Exception e) when (e is InvalidOperationException
                                       or System.ComponentModel.Win32Exception
                                       or ArgumentException)
            {
                // The process exited between enumeration and inspection. Normal, not an error.
            }
            finally
            {
                process.Dispose();
            }
        }

        _cache = result;
        _refreshedAt = now;
        return result;
    }

    private static CallApp Identify(Process process)
    {
        var name = SafeName(process);
        if (name is null) return CallApp.Unknown;

        // Package identity first: it is immune to renames and to the versioned install path.
        if (TryGetPackageFamilyName(process, out var family))
        {
            foreach (var (candidate, app) in PackageFamilies)
            {
                if (family.Equals(candidate, StringComparison.OrdinalIgnoreCase)) return app;
            }
        }

        if (WhatsAppExecutables.Contains(name, StringComparer.OrdinalIgnoreCase)) return CallApp.WhatsApp;
        if (TelegramExecutables.Contains(name, StringComparer.OrdinalIgnoreCase)) return CallApp.Telegram;
        if (SignalExecutables.Contains(name, StringComparer.OrdinalIgnoreCase)) return CallApp.Signal;

        return CallApp.Unknown;
    }

    private static string? SafeName(Process process)
    {
        try
        {
            return process.ProcessName + ".exe";
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Every descendant of a process.
    ///
    /// Matching msedgewebview2.exe by name would be wrong: on this machine alone, Windows
    /// Search, Microsoft 365 Copilot and Google Drive each run their own WebView2 host. Only
    /// the parent chain says which one belongs to the app being watched.
    /// </summary>
    private static IEnumerable<int> DescendantsOf(int rootId)
    {
        Dictionary<int, List<int>> childrenByParent;

        try
        {
            childrenByParent = ChildIndex();
        }
        catch (ManagementException)
        {
            // WMI unavailable. The root process alone is still a useful answer.
            yield break;
        }

        var queue = new Queue<int>();
        queue.Enqueue(rootId);
        var seen = new HashSet<int> { rootId };

        while (queue.Count > 0)
        {
            if (!childrenByParent.TryGetValue(queue.Dequeue(), out var children)) continue;

            foreach (var child in children)
            {
                if (!seen.Add(child)) continue;
                queue.Enqueue(child);
                yield return child;
            }
        }
    }

    private static Dictionary<int, List<int>>? _childIndex;
    private static DateTimeOffset _childIndexAt = DateTimeOffset.MinValue;

    private static Dictionary<int, List<int>> ChildIndex()
    {
        // One WMI query answers the parent question for every process at once, which is far
        // cheaper than asking per process.
        if (_childIndex is not null && DateTimeOffset.UtcNow - _childIndexAt < TimeSpan.FromSeconds(5))
            return _childIndex;

        var index = new Dictionary<int, List<int>>();

        using var searcher = new ManagementObjectSearcher(
            "SELECT ProcessId, ParentProcessId FROM Win32_Process");

        foreach (var row in searcher.Get().Cast<ManagementObject>())
        {
            using (row)
            {
                var pid = Convert.ToInt32(row["ProcessId"]);
                var parent = Convert.ToInt32(row["ParentProcessId"]);

                if (!index.TryGetValue(parent, out var list)) index[parent] = list = [];
                list.Add(pid);
            }
        }

        _childIndex = index;
        _childIndexAt = DateTimeOffset.UtcNow;
        return index;
    }

    private static bool TryGetPackageFamilyName(Process process, out string family)
    {
        family = "";

        var handle = OpenProcess(ProcessQueryLimitedInformation, false, process.Id);
        if (handle == nint.Zero) return false;

        try
        {
            uint length = 0;
            _ = GetPackageFamilyName(handle, ref length, null);
            if (length == 0) return false;

            var buffer = new char[length];
            if (GetPackageFamilyName(handle, ref length, buffer) != 0) return false;

            family = new string(buffer, 0, (int)length - 1);
            return true;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFamilyName(nint process, ref uint length, char[]? name);
}
