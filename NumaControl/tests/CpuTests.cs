using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace NumaControl
{
    internal static partial class Tests
    {
        private static bool Near(double actual, double expected) { return Math.Abs(actual - expected) < 0.000001; }
        private static void CpuAndNativeChecks()
        {
            CpuMeter meter = new CpuMeter(0, 10);
            meter.Observe(1, 10.6);
            Check(meter.IntervalSeconds == 0, "CPU meter: manual refresh before five seconds cannot create a short spike");
            meter.Observe(5, 13);
            Check(Near(meter.Percent, 60) && Near(meter.Percent / 16, 3.75) && Near(meter.IntervalSeconds, 5), "CPU meter: 3 CPU seconds in 5 wall seconds = 60 percent core / 3.75 percent machine on 16 CPUs");
            for (int i = 1; i < 50; i++) meter.Observe(5 + i / 10.0, 13 + i * 0.02);
            Check(Near(meter.Percent, 60) && Near(meter.AverageSeconds, 5), "CPU meter: frequent refresh reuses interval without duplicating rolling history");
            meter.Observe(10, 14);
            Check(Near(meter.Percent, 20) && Near(meter.AveragePercent, 40), "CPU meter: successive intervals use cumulative differences");
            meter.Observe(17, 14.7);
            Check(Near(meter.Percent, 10) && Near(meter.AveragePercent, 100 * 4.7 / 17), "CPU meter: delayed timer uses real elapsed time and weighted average");
            meter.Observe(22, 14.7); meter.Observe(27, 14.7); meter.Observe(32, 14.7);
            Check(Near(meter.Percent, 0) && Near(meter.AveragePercent, 100 * 3.5 / 30) && Near(meter.AverageSeconds, 30), "CPU meter: rolling window clips boundary proportionally and expires idle history");
            meter.Observe(37, 14.7); meter.Observe(42, 14.7); meter.Observe(47, 14.7);
            Check(Near(meter.AveragePercent, 0), "CPU meter: old burst leaves the 30-second window completely");
            meter.Observe(100, 15.23);
            Check(Near(meter.Percent, 1) && Near(meter.AveragePercent, 1), "CPU meter: suspend-sized gap clears earlier rolling intervals");
            Reject(() => meter.Observe(99, 16), "CPU meter: backwards wall clock rejected");
            Reject(() => meter.Observe(101, 14), "CPU meter: backwards CPU counter rejected");
            CpuMeter multicore = new CpuMeter(0, 0); multicore.Observe(5, 12.5);
            Check(Near(multicore.Percent, 250), "CPU meter: summed multi-thread CPU may exceed 100 percent of one logical CPU");

            using (Process own = Process.GetCurrentProcess())
            {
                OwnResources native = OwnResources.Read();
                double managedCpu = own.TotalProcessorTime.TotalSeconds;
                Check(Math.Abs(native.CpuSeconds - managedCpu) < 0.1, "native diagnostics: CPU seconds agree with independent managed Windows API");
                own.Refresh();
                Check(Math.Abs(native.PrivateMiB - own.PrivateMemorySize64 / 1048576.0) < 8 && Math.Abs(native.WorkingMiB - own.WorkingSet64 / 1048576.0) < 8, "native diagnostics: private bytes and working set agree with managed reference");
                Check(Math.Abs(native.Handles - own.HandleCount) < 32, "native diagnostics: handle count agrees with reference");
                ProcessSnapshot snapshot = new ProcessSnapshot();
                Process[] matches = snapshot.Find(own.ProcessName.ToUpperInvariant());
                try { Check(matches.Any(p => p.Id == own.Id), "native process list: case insensitive exact executable name finds own PID"); }
                finally { foreach (Process p in matches) p.Dispose(); }
                Check(snapshot.Find("NumaDefinitelyMissingProcess_1938").Length == 0, "native process list: absent executable returns no candidates");
                Check(snapshot.Find(own.ProcessName + "-suffix").Length == 0, "native process list: partial names do not match");
                Check(Math.Abs(snapshot.OwnThreads - own.Threads.Count) < 16, "native process list: thread field layout matches Windows thread count");
                Check(ProcessSnapshot.Session(own.Id) == own.SessionId, "native process list: session matches Windows managed reference");
                using (var handle = Native.Open(own.Id, false)) Check(string.Equals(Native.Image(handle), own.MainModule.FileName, StringComparison.OrdinalIgnoreCase), "small image path buffer preserves verified full image identity");
                int before = own.HandleCount;
                for (int i = 0; i < 100; i++) { new ProcessSnapshot(); OwnResources.Read(); }
                own.Refresh(); Check(own.HandleCount - before < 16, "native diagnostics: 100 process snapshots and resource samples do not leak native handles");
            }
        }
    }
}
