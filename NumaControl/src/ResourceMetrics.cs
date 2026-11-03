using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace NumaControl
{
    internal sealed class OwnResources
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryCounters
        {
            internal uint Size, PageFaults;
            internal UIntPtr PeakWorking, Working, PeakPaged, Paged, PeakNonPaged, NonPaged, Pagefile, PeakPagefile, Private;
        }
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(IntPtr process, out long created, out long exited, out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessHandleCount(IntPtr process, out uint count);
        [DllImport("psapi.dll", SetLastError = true)] private static extern bool GetProcessMemoryInfo(IntPtr process, ref MemoryCounters memory, uint size);
        internal double CpuSeconds, WorkingMiB, PrivateMiB;
        internal int Handles;
        internal static OwnResources Read()
        {
            IntPtr self = GetCurrentProcess(); // Pseudo handle: never owned or closed.
            long created, exited, kernel, user; uint handles;
            MemoryCounters memory = new MemoryCounters { Size = (uint)Marshal.SizeOf(typeof(MemoryCounters)) };
            if (!GetProcessTimes(self, out created, out exited, out kernel, out user) ||
                !GetProcessHandleCount(self, out handles) || !GetProcessMemoryInfo(self, ref memory, memory.Size))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return new OwnResources { CpuSeconds = (kernel + user) / 10000000.0,
                WorkingMiB = memory.Working.ToUInt64() / 1048576.0, PrivateMiB = memory.Private.ToUInt64() / 1048576.0, Handles = checked((int)handles) };
        }
    }
    // Cumulative process CPU / monotonic wall time. Manual refresh cannot shorten
    // the >=5 s measurement interval or count the same CPU time twice.
    internal sealed class CpuMeter
    {
        private struct Segment { internal double End, Seconds, Cpu; }
        private readonly Queue<Segment> segments = new Queue<Segment>();
        private double previousTime, previousCpu, lastObservation;
        internal double Percent { get; private set; }
        internal double AveragePercent { get; private set; }
        internal double IntervalSeconds { get; private set; }
        internal double AverageSeconds { get; private set; }
        internal CpuMeter(double seconds, double cpu) { previousTime = lastObservation = seconds; previousCpu = cpu; }
        internal void Observe(double seconds, double cpu)
        {
            if (seconds < lastObservation || cpu < previousCpu) throw new ArgumentException("CPU counters must be monotonic.");
            lastObservation = seconds;
            if (seconds - previousTime < 5) return;
            double elapsed = seconds - previousTime, used = cpu - previousCpu;
            Percent = 100 * used / elapsed; IntervalSeconds = elapsed;
            if (elapsed > 15) segments.Clear(); // Do not mix suspend/resume with the preceding window.
            segments.Enqueue(new Segment { End = seconds, Seconds = elapsed, Cpu = used });
            previousTime = seconds; previousCpu = cpu;
            while (segments.Count > 1 && seconds - segments.Peek().End >= 30) segments.Dequeue();
            double totalTime = 0, totalCpu = 0;
            foreach (Segment s in segments)
            {
                double included = Math.Min(s.Seconds, Math.Max(0, 30 - (seconds - s.End)));
                totalTime += included; totalCpu += s.Cpu * included / s.Seconds;
            }
            AverageSeconds = totalTime; AveragePercent = totalTime == 0 ? 0 : 100 * totalCpu / totalTime;
        }
    }
}
