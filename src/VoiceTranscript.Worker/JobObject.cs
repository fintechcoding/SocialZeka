using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VoiceTranscript.Worker;

/// <summary>
/// A Win32 job object configured to kill everything inside it when the handle closes.
///
/// This exists for one reason: the Python worker can be holding gigabytes of GPU memory, and if
/// this application is force-killed — Task Manager, a crash, a debugger stop — the ordinary
/// cleanup paths never run and the worker is orphaned. The next recording then fails to
/// allocate, and the user has a phantom process eating their VRAM with no obvious cause.
///
/// A job object is the only mechanism that survives that. From the Microsoft documentation:
/// when JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE is set, "closing the last job object handle
/// terminates all associated processes". The kernel closes our handles however we die, so the
/// worker dies with us unconditionally.
///
/// Child processes of the worker are captured too, which matters because a transcription
/// backend may spawn its own helpers. Neither breakaway flag is set, so the whole tree is
/// covered.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class JobObject : IDisposable
{
    private const int ExtendedLimitInformation = 9;
    private const uint LimitKillOnJobClose = 0x2000;

    private nint _handle;
    private bool _disposed;

    public JobObject(string? name = null)
    {
        _handle = CreateJobObjectW(nint.Zero, name);
        if (_handle == nint.Zero)
            throw new InvalidOperationException($"CreateJobObject failed ({Marshal.GetLastWin32Error()})");

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = LimitKillOnJobClose },
        };

        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, fDeleteOld: false);

            if (!SetInformationJobObject(_handle, ExtendedLimitInformation, buffer, (uint)size))
            {
                var error = Marshal.GetLastWin32Error();
                CloseHandle(_handle);
                _handle = nint.Zero;
                throw new InvalidOperationException($"SetInformationJobObject failed ({error})");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Puts a process under this job. Once assigned the association cannot be broken.
    /// </summary>
    public void Assign(nint processHandle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!AssignProcessToJobObject(_handle, processHandle))
            throw new InvalidOperationException($"AssignProcessToJobObject failed ({Marshal.GetLastWin32Error()})");
    }

    /// <summary>True when the process is inside this job. Used by the tests.</summary>
    public bool Contains(nint processHandle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return IsProcessInJob(processHandle, _handle, out var inJob) && inJob;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_handle != nint.Zero)
        {
            // Closing the last handle is what terminates the contained processes.
            CloseHandle(_handle);
            _handle = nint.Zero;
        }

        GC.SuppressFinalize(this);
    }

    ~JobObject() => Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateJobObjectW(nint securityAttributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int infoClass, nint info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessInJob(nint process, nint job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
