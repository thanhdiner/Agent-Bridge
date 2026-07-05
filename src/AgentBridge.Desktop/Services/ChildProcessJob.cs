using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgentBridge.Desktop.Services;

/// <summary>
/// Keeps managed service child processes tied to the Desktop lifetime.
/// If AgentBridge Desktop is closed or killed, Windows closes the job handle and
/// terminates Gateway, Agent, Tunnel, and their child process trees.
/// </summary>
public sealed class ChildProcessJob : IDisposable
{
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const int JobObjectLimitKillOnJobClose = 0x00002000;

    private readonly SafeFileHandle _handle;
    private bool _disposed;

    private ChildProcessJob(SafeFileHandle handle)
    {
        _handle = handle;
    }

    public static ChildProcessJob Create(string name)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows job objects are only available on Windows.");

        var handle = CreateJobObject(nint.Zero, name);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create AgentBridge child process job object.");

        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose
            }
        };

        var length = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var pointer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(limits, pointer, false);
            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformationClass, pointer, (uint)length))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to configure AgentBridge child process job object.");
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }

        return new ChildProcessJob(handle);
    }

    public bool TryAssign(Process process, out string? errorMessage)
    {
        errorMessage = null;
        if (_disposed)
        {
            errorMessage = "Child process job object has already been disposed.";
            return false;
        }

        try
        {
            if (AssignProcessToJobObject(_handle, process.Handle))
                return true;

            errorMessage = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _handle.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObject(nint lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle hJob,
        int jobObjectInfoClass,
        nint lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle hJob, nint hProcess);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public int LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
