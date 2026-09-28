using System.Runtime.InteropServices;

namespace SysPulse.Core.Processes;

/// <summary>
/// Samples every running process's identity, CPU time, and memory usage via a single
/// <c>NtQuerySystemInformation(SystemProcessInformation)</c> call per sample (ntdll). Unlike
/// <c>Process.GetProcesses()</c>, this opens no per-process handle, so elevated processes are
/// still enumerated and no processes are silently skipped due to access-denied handle opens.
/// </summary>
/// <remarks>
/// <para>
/// The native query buffer is a single pooled field, grown (never shrunk) and reused across
/// calls to avoid allocation growth on every sample: <see cref="Sample"/> only reallocates on
/// <c>STATUS_INFO_LENGTH_MISMATCH</c> (0xC0000004), i.e. when the process count has grown enough
/// that the previous buffer no longer fits.
/// </para>
/// <para>
/// <b><c>SYSTEM_PROCESS_INFORMATION</c> field offsets.</b> This struct is undocumented by
/// Microsoft; the offsets below were verified against the ProcessHacker/System Informer
/// <c>phnt</c> headers (the de facto reference for this struct) and by manual layout arithmetic.
/// x64 and ARM64 Windows share an 8-byte pointer size and identical natural-alignment rules, so
/// <see cref="LayoutKind.Sequential"/> produces this exact same layout on both architectures —
/// there is no architecture-specific divergence to special-case.
/// <code>
/// 0x00 NextEntryOffset              uint
/// 0x04 NumberOfThreads              uint
/// 0x08 WorkingSetPrivateSize        long   (LARGE_INTEGER, since Vista)
/// 0x10 HardFaultCount               uint   (since Win7)
/// 0x14 NumberOfThreadsHighWatermark uint   (since Win7)
/// 0x18 CycleTime                    ulong  (since Win7)
/// 0x20 CreateTime                   long   (LARGE_INTEGER / FILETIME ticks)          -&gt; ProcessSample.CreateTime
/// 0x28 UserTime                     long   (LARGE_INTEGER)                            -&gt; CPU ticks
/// 0x30 KernelTime                   long   (LARGE_INTEGER)                            -&gt; CPU ticks
/// 0x38 ImageName                    UNICODE_STRING (ushort Length, ushort MaxLength,
///                                   4 bytes padding, nint Buffer = 16 bytes total)      -&gt; ProcessSample.Name
/// 0x48 BasePriority                 int
/// 0x50 UniqueProcessId              nint   (8-byte aligned; padded up from 0x4C)        -&gt; ProcessSample.Pid
/// 0x58 InheritedFromUniqueProcessId nint
/// 0x60 HandleCount                  uint
/// 0x64 SessionId                    uint
/// 0x68 UniqueProcessKey             nuint
/// 0x70 PeakVirtualSize              nuint
/// 0x78 VirtualSize                  nuint
/// 0x80 PageFaultCount               uint
/// 0x88 PeakWorkingSetSize           nuint  (8-byte aligned; padded up from 0x84)
/// 0x90 WorkingSetSize               nuint                                              -&gt; ProcessSample.WorkingSetBytes
/// 0x98 QuotaPeakPagedPoolUsage      nuint
/// 0xA0 QuotaPagedPoolUsage          nuint
/// 0xA8 QuotaPeakNonPagedPoolUsage   nuint
/// 0xB0 QuotaNonPagedPoolUsage       nuint
/// 0xB8 PagefileUsage                nuint
/// 0xC0 PeakPagefileUsage            nuint
/// 0xC8 PrivatePageCount             nuint                                              -&gt; ProcessSample.PrivateBytes
/// </code>
/// Despite its name, <c>PrivatePageCount</c> (like <c>WorkingSetSize</c> and the other
/// Quota*/PagefileUsage fields) is reported in <b>bytes</b>, not pages — a long-standing naming
/// artifact inherited from the struct's original (pre-WOW64) definition; this is how
/// ProcessHacker/System Informer treat the field (as <c>ProcessPrivateBytes</c>) and matches
/// <c>Process.PrivateMemorySize64</c> for the same process.
/// </para>
/// <para>
/// The managed struct below declares fields only through <c>PrivatePageCount</c> — everything
/// SysPulse needs. The native struct's trailing <c>SYSTEM_THREAD_INFORMATION[]</c> array (per
/// thread) is never read, and entries are walked via <c>NextEntryOffset</c> rather than
/// <c>sizeof(SYSTEM_PROCESS_INFORMATION)</c>, so the managed struct being a byte-for-byte prefix
/// of the real (larger) native entry is safe.
/// </para>
/// </remarks>
public sealed partial class ProcessSampler : IProcessSampler, IDisposable
{
    private const int SystemProcessInformation = 5;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int InitialBufferBytes = 256 * 1024;
    private const int GrowthPaddingBytes = 8192;
    private const int MaxGrowthAttempts = 16;

    private readonly TimeProvider _timeProvider;
    private readonly ProcessCpuTracker _cpuTracker = new();

    // Serializes Sample() and Dispose(): the native buffer must never be freed (or regrown by a
    // concurrent caller) while another thread is walking it.
    private readonly Lock _gate = new();
    private nint _buffer;
    private int _bufferSize;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProcessSampler"/> class and allocates the
    /// initial pooled query buffer.
    /// </summary>
    /// <param name="timeProvider">The time source used to compute CPU-percent wall-clock deltas. Defaults to <see cref="TimeProvider.System"/>.</param>
    public ProcessSampler(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _bufferSize = InitialBufferBytes;
        _buffer = Marshal.AllocHGlobal(_bufferSize);
    }

    /// <inheritdoc />
    /// <remarks>Thread-safe: concurrent calls are serialized; after <see cref="Dispose"/> it throws <see cref="ObjectDisposedException"/>.</remarks>
    public IReadOnlyList<ProcessSample> Sample()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return SampleCore();
        }
    }

    private unsafe List<ProcessSample> SampleCore()
    {

        DateTimeOffset timestamp = _timeProvider.GetUtcNow();
        int usedLength = Query();

        var rawSamples = new List<RawSample>();
        var cpuInputs = new List<ProcessCpuSample>();

        byte* basePtr = (byte*)_buffer;
        byte* end = basePtr + usedLength;
        byte* cursor = basePtr;

        while (cursor + sizeof(SYSTEM_PROCESS_INFORMATION) <= end)
        {
            var entry = (SYSTEM_PROCESS_INFORMATION*)cursor;
            int pid = unchecked((int)entry->UniqueProcessId);
            string name = ReadName(pid, in entry->ImageName);
            ulong cpuTicks = unchecked((ulong)entry->UserTime) + unchecked((ulong)entry->KernelTime);

            rawSamples.Add(new RawSample(pid, name, entry->CreateTime, (ulong)entry->PrivatePageCount, (ulong)entry->WorkingSetSize));
            cpuInputs.Add(new ProcessCpuSample(pid, entry->CreateTime, cpuTicks));

            if (entry->NextEntryOffset == 0)
            {
                break;
            }

            cursor += entry->NextEntryOffset;
        }

        IReadOnlyDictionary<int, double> cpuPercents = _cpuTracker.ComputeCpuPercents(cpuInputs, timestamp, Environment.ProcessorCount);

        var results = new List<ProcessSample>(rawSamples.Count);
        foreach (RawSample raw in rawSamples)
        {
            double cpuPercent = cpuPercents.TryGetValue(raw.Pid, out double percent) ? percent : 0.0;
            results.Add(new ProcessSample(raw.Pid, raw.Name, raw.CreateTime, cpuPercent, raw.PrivateBytes, raw.WorkingSetBytes));
        }

        return results;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_buffer != 0)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = 0;
            }

            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }

    private static unsafe string ReadName(int pid, in UNICODE_STRING imageName)
    {
        if (pid == 0)
        {
            // Spec §5.1: PID 0's raw ImageName is empty; display it as "Idle".
            return "Idle";
        }

        if (imageName.Length == 0 || imageName.Buffer == 0)
        {
            return string.Empty;
        }

        return new string((char*)imageName.Buffer, 0, imageName.Length / 2);
    }

    /// <summary>
    /// Issues the native query, growing (and replacing) the pooled buffer on
    /// <c>STATUS_INFO_LENGTH_MISMATCH</c> until it succeeds.
    /// </summary>
    /// <returns>The number of bytes in <see cref="_buffer"/> actually populated by the kernel.</returns>
    private int Query()
    {
        for (int attempt = 0; attempt < MaxGrowthAttempts; attempt++)
        {
            int status = NtQuerySystemInformation(SystemProcessInformation, _buffer, _bufferSize, out int returnLength);
            if (status == 0)
            {
                return returnLength > 0 ? returnLength : _bufferSize;
            }

            if (status != StatusInfoLengthMismatch)
            {
                throw new InvalidOperationException($"NtQuerySystemInformation failed with NTSTATUS 0x{status:X8}.");
            }

            int newSize = returnLength > _bufferSize ? returnLength + GrowthPaddingBytes : _bufferSize * 2;
            Grow(newSize);
        }

        throw new InvalidOperationException("NtQuerySystemInformation did not stabilize after repeated buffer growth.");
    }

    private void Grow(int newSize)
    {
        nint newBuffer = Marshal.AllocHGlobal(newSize);
        Marshal.FreeHGlobal(_buffer);
        _buffer = newBuffer;
        _bufferSize = newSize;
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtQuerySystemInformation(int systemInformationClass, nint systemInformation, int systemInformationLength, out int returnLength);

    private readonly record struct RawSample(int Pid, string Name, long CreateTime, ulong PrivateBytes, ulong WorkingSetBytes);

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_PROCESS_INFORMATION
    {
        public uint NextEntryOffset;
        public uint NumberOfThreads;
        public long WorkingSetPrivateSize;
        public uint HardFaultCount;
        public uint NumberOfThreadsHighWatermark;
        public ulong CycleTime;
        public long CreateTime;
        public long UserTime;
        public long KernelTime;
        public UNICODE_STRING ImageName;
        public int BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
        public uint HandleCount;
        public uint SessionId;
        public nuint UniqueProcessKey;
        public nuint PeakVirtualSize;
        public nuint VirtualSize;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivatePageCount;
    }
}
