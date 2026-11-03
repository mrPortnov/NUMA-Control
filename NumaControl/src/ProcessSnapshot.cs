using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NumaControl
{
    // One native process list per watcher cycle, shared by all game rules and self affinity.
    // Managed Process.GetProcessesByName repeats the system-wide enumeration for each name.
    internal sealed class ProcessSnapshot
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Entry
        {
            internal uint Size, Usage, Pid;
            internal UIntPtr Heap;
            internal uint Module, Threads, Parent;
            internal int Priority;
            internal uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string Name;
        }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool First(SafeFileHandle snapshot, ref Entry entry);
        [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Next(SafeFileHandle snapshot, ref Entry entry);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ProcessIdToSessionId(uint pid, out uint session);
        private readonly Dictionary<string, List<int>> names = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        internal int OwnThreads { get; private set; }
        internal ProcessSnapshot()
        {
            int ownPid; using (Process own = Process.GetCurrentProcess()) ownPid = own.Id;
            using (SafeFileHandle snapshot = CreateToolhelp32Snapshot(2, 0))
            {
                if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                Entry entry = new Entry { Size = (uint)Marshal.SizeOf(typeof(Entry)) };
                bool found = First(snapshot, ref entry);
                while (found)
                {
                    if (entry.Pid == ownPid) OwnThreads = checked((int)entry.Threads);
                    List<int> pids;
                    if (!names.TryGetValue(entry.Name, out pids)) names.Add(entry.Name, pids = new List<int>());
                    pids.Add(checked((int)entry.Pid));
                    found = Next(snapshot, ref entry);
                }
                int error = Marshal.GetLastWin32Error();
                if (error != 18) throw new Win32Exception(error); // ERROR_NO_MORE_FILES
            }
        }
        internal Process[] Find(string name)
        {
            List<int> pids;
            if (!names.TryGetValue(name + ".exe", out pids)) return new Process[0];
            List<Process> result = new List<Process>();
            foreach (int pid in pids)
                try { result.Add(Process.GetProcessById(pid)); } catch (ArgumentException) { } // Exited since the snapshot.
            return result.ToArray();
        }
        internal static int Session(int pid)
        {
            uint session;
            if (!ProcessIdToSessionId((uint)pid, out session)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return checked((int)session);
        }
    }
}
