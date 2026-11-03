using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Threading;

namespace NumaControl
{
    public sealed class ResourceSample
    {
        public string TimeUtc { get; set; }
        public double ElapsedSeconds { get; set; }
        public double WorkingMiB { get; set; }
        public double PrivateMiB { get; set; }
        public double ManagedMiB { get; set; }
        public double CpuOneCorePercent { get; set; }
        public double CpuMachinePercent { get; set; }
        public double CpuAverageOneCorePercent { get; set; }
        public double CpuAverageMachinePercent { get; set; }
        public double CpuIntervalSeconds { get; set; }
        public double CpuAverageSeconds { get; set; }
        public double CpuTotalSeconds { get; set; }
        public int Handles { get; set; }
        public int Threads { get; set; }
        public double UiHeartbeatAgeSeconds { get; set; }
        public double EngineHeartbeatAgeSeconds { get; set; }
        public int EngineErrors { get; set; }
        public bool EngineAlive { get; set; }
        public bool EngineRequired { get; set; }
        public bool WindowVisible { get; set; }
        public bool AppAffinityError { get; set; }
    }
    internal sealed class HealthLimits
    {
        internal double PrivateMiB = 512, WorkingMiB = 768;
        internal int Handles = 6000;
        internal double SustainedSeconds = 30, HiddenCpuPercent = 50, VisibleCpuPercent = 90, CpuSeconds = 60;
        internal double UiTimeoutSeconds = 45, EngineTimeoutSeconds = 90;
        internal int MaxCycleErrors = 5;
        internal double TrendWindowSeconds = 300, TrendWarmupSeconds = 60, MemoryGrowthMiB = 96;
        internal int HandleGrowth = 2000;
    }
    internal sealed class HealthPolicy
    {
        private readonly HealthLimits limits;
        private readonly Dictionary<string, double> since = new Dictionary<string, double>();
        private readonly Queue<ResourceSample> trend = new Queue<ResourceSample>();
        private double previousTime = -1, warmupUntil;
        private bool previousVisible;
        private bool visibleKnown;
        internal HealthPolicy(HealthLimits settings) { limits = settings; }
        private bool Sustained(string key, bool exceeded, double now, double seconds)
        {
            if (!exceeded) { since.Remove(key); return false; }
            double start;
            if (!since.TryGetValue(key, out start)) { since[key] = now; return false; }
            return now - start >= seconds;
        }
        internal string Observe(ResourceSample s)
        {
            if (s.ElapsedSeconds < previousTime) throw new ArgumentException("Health samples must use monotonic time.");
            if (previousTime >= 0 && s.ElapsedSeconds - previousTime > 15) { since.Clear(); trend.Clear(); visibleKnown = false; }
            previousTime = s.ElapsedSeconds;
            if (s.AppAffinityError) return "app-affinity-failure";
            if (s.UiHeartbeatAgeSeconds > limits.UiTimeoutSeconds) return "ui-unresponsive";
            if (s.EngineRequired && (!s.EngineAlive || s.EngineHeartbeatAgeSeconds > limits.EngineTimeoutSeconds)) return "engine-unresponsive";
            if (s.EngineErrors >= limits.MaxCycleErrors) return "repeated-engine-errors";
            if (Sustained("private", s.PrivateMiB >= limits.PrivateMiB, s.ElapsedSeconds, limits.SustainedSeconds)) return "private-memory-limit";
            if (Sustained("working", s.WorkingMiB >= limits.WorkingMiB, s.ElapsedSeconds, limits.SustainedSeconds)) return "working-memory-limit";
            if (Sustained("handles", s.Handles >= limits.Handles, s.ElapsedSeconds, limits.SustainedSeconds)) return "handle-limit";
            double cpuLimit = s.WindowVisible ? limits.VisibleCpuPercent : limits.HiddenCpuPercent;
            if (Sustained("cpu", s.CpuOneCorePercent >= cpuLimit, s.ElapsedSeconds, s.WindowVisible ? limits.CpuSeconds * 2 : limits.CpuSeconds)) return "sustained-high-cpu";
            if (!visibleKnown || s.WindowVisible != previousVisible)
            {
                trend.Clear(); warmupUntil = s.ElapsedSeconds + limits.TrendWarmupSeconds; previousVisible = s.WindowVisible; visibleKnown = true;
            }
            if (s.ElapsedSeconds < warmupUntil) return null;
            trend.Enqueue(s);
            while (trend.Count > 180 || (trend.Count > 1 && s.ElapsedSeconds - trend.Peek().ElapsedSeconds > limits.TrendWindowSeconds + 5)) trend.Dequeue();
            if (trend.Count < 12 || s.ElapsedSeconds - trend.Peek().ElapsedSeconds < limits.TrendWindowSeconds) return null;
            ResourceSample[] values = trend.ToArray();
            int risingMemory = 0, risingHandles = 0;
            for (int i = 1; i < values.Length; i++)
            {
                if (values[i].PrivateMiB - values[i - 1].PrivateMiB >= 0.25) risingMemory++;
                if (values[i].Handles - values[i - 1].Handles >= 2) risingHandles++;
            }
            if (s.PrivateMiB - values[0].PrivateMiB >= limits.MemoryGrowthMiB && risingMemory >= (values.Length - 1) * 0.6) return "suspected-memory-leak";
            if (s.Handles - values[0].Handles >= limits.HandleGrowth && risingHandles >= (values.Length - 1) * 0.6) return "suspected-handle-leak";
            return null;
        }
    }

    internal sealed class BugReports
    {
        internal readonly string DirectoryPath;
        internal readonly string FallbackDataDirectory;
        internal readonly string EmergencyDataDirectory;
        internal string FailureMarker { get { return Path.Combine(DirectoryPath, "last-failure.json"); } }
        private readonly AppLog log;
        private readonly object gate = new object();
        internal BugReports(string dataDirectory, AppLog appLog)
        {
            DirectoryPath = Path.Combine(dataDirectory, "bug-reports"); log = appLog;
            string identity;
            using (var hash = System.Security.Cryptography.SHA256.Create()) identity = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(dataDirectory).ToUpperInvariant()))).Replace("-", "").Substring(0, 16);
            FallbackDataDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "recovery", identity);
            EmergencyDataDirectory = Path.Combine(Path.GetTempPath(), "NumaControl-recovery", identity);
        }
        internal bool PreviousFailure { get { return File.Exists(FailureMarker); } }
        internal string Write(string reason, Exception error, IEnumerable<ResourceSample> samples, object context, bool fatal)
        {
            lock (gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                string name = (fatal ? "failure-" : "diagnostic-") + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".json";
                string tail;
                try { tail = log.Tail(); } catch (Exception logError) { tail = L10n.T("Журнал недоступен: ") + logError.GetType().Name; }
                if (tail.Length > 65536) tail = tail.Substring(0, 65536);
                string stack = error == null ? null : error.ToString();
                if (stack != null && stack.Length > 32768) stack = stack.Substring(0, 32768);
                object report = new {
                    Schema = 1, Kind = fatal ? "fatal" : "manual", Reason = reason, TimeUtc = DateTime.UtcNow.ToString("o"),
                    Version = Assembly.GetExecutingAssembly().GetName().Version.ToString(), Runtime = Environment.Version.ToString(),
                    Windows = Environment.OSVersion.VersionString, Processors = Environment.ProcessorCount,
                    Exception = stack, Context = context, Samples = samples.Take(180).ToArray(), RecentLog = tail,
                    Privacy = "Local report only. No game memory, process dump, complete profiles, launch arguments or automatic upload. Logs may contain local paths."
                };
                string path = Path.Combine(DirectoryPath, name);
                ProfileStore.WriteAtomic(path, ProfileStore.Serializer().Serialize(report));
                if (fatal) ProfileStore.WriteAtomic(FailureMarker, ProfileStore.Serializer().Serialize(new { Report = name, Reason = reason, TimeUtc = DateTime.UtcNow.ToString("o") }));
                foreach (FileInfo old in new DirectoryInfo(DirectoryPath).GetFiles("*.json").Where(f => f.Name.StartsWith("failure-", StringComparison.Ordinal) || f.Name.StartsWith("diagnostic-", StringComparison.Ordinal)).OrderByDescending(f => f.LastWriteTimeUtc).Skip(20))
                    try { old.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                return path;
            }
        }
        internal void ClearFailure() { if (File.Exists(FailureMarker)) File.Delete(FailureMarker); }
    }

    internal sealed class SelfDiagnostics : IDisposable
    {
        private readonly Dispatcher dispatcher;
        private readonly WatchEngine engine;
        private readonly Func<object> context;
        private readonly Action<string> stop;
        private readonly Action armStop;
        private readonly BugReports reports;
        private readonly AppLog log;
        private readonly HealthPolicy policy;
        private readonly Queue<ResourceSample> history = new Queue<ResourceSample>();
        private ResourceSample current;
        private readonly object gate = new object();
        private readonly Stopwatch uptime = Stopwatch.StartNew();
        private readonly CpuMeter cpuMeter;
        private readonly int machineProcessors = Math.Max(1, checked((int)Native.GetActiveProcessorCount(0xffff)));
        private readonly Thread monitor;
        private readonly ManualResetEvent finish = new ManualResetEvent(false);
        private long uiBeat = Stopwatch.GetTimestamp();
        private int pendingBeat, fatal, disposed;
        private int sampling;
        private int visible;
        private double previousElapsed = -1, historyElapsed = -5;
        private double threadSampleTime = -30;
        private int previewThreads;
        private double suspendGraceUntil;
        internal event Action Sampled;
        internal readonly string SessionId = Guid.NewGuid().ToString("N");
        internal string LastReport { get; private set; }
        internal bool IsFatal { get { return Volatile.Read(ref fatal) != 0; } }
        internal ResourceSample Current { get { lock (gate) return current; } }
        internal ResourceSample[] History { get { lock (gate) return history.ToArray(); } }
        internal SelfDiagnostics(Dispatcher ui, WatchEngine watcher, BugReports writer, AppLog appLog, Func<object> details, Action<string> shutdown, HealthLimits limits = null, Action prepareStop = null)
        {
            dispatcher = ui; engine = watcher; reports = writer; log = appLog; context = details; stop = shutdown; armStop = prepareStop;
            policy = new HealthPolicy(limits ?? new HealthLimits());
            cpuMeter = new CpuMeter(uptime.Elapsed.TotalSeconds, OwnResources.Read().CpuSeconds);
            monitor = new Thread(Monitor) { IsBackground = true, Name = "Self diagnostics", Priority = ThreadPriority.BelowNormal };
        }
        internal void SetWindowVisible(bool value) { Volatile.Write(ref visible, value ? 1 : 0); }
        internal void Start() { monitor.Start(); }
        internal static double Age(long timestamp) { return Math.Max(0, (Stopwatch.GetTimestamp() - timestamp) / (double)Stopwatch.Frequency); }
        private void Monitor()
        {
            while (!finish.WaitOne(5000))
            {
                if (Volatile.Read(ref disposed) != 0) return;
                try { Sample(); }
                catch (Exception error) { Fatal("diagnostics-error", error); }
            }
        }
        internal void Sample()
        {
            if (Interlocked.CompareExchange(ref sampling, 1, 0) != 0) return;
            try { CollectSample(); } finally { Interlocked.Exchange(ref sampling, 0); }
        }
        private void CollectSample()
        {
            if (Volatile.Read(ref disposed) != 0 || IsFatal) return;
            double elapsed = uptime.Elapsed.TotalSeconds;
            if (elapsed - previousElapsed < 1) return; // Bound work even when Refresh is pressed repeatedly.
            OwnResources resources = OwnResources.Read();
            if (elapsed - previousElapsed > 15) { suspendGraceUntil = elapsed + 95; Interlocked.Exchange(ref uiBeat, Stopwatch.GetTimestamp()); }
            cpuMeter.Observe(elapsed, resources.CpuSeconds);
            double oneCore = cpuMeter.Percent;
            if (engine == null && elapsed - threadSampleTime >= 30) { previewThreads = new ProcessSnapshot().OwnThreads; threadSampleTime = elapsed; }
            previousElapsed = elapsed;
            ResourceSample sample = new ResourceSample {
                TimeUtc = DateTime.UtcNow.ToString("o"), ElapsedSeconds = elapsed,
                WorkingMiB = resources.WorkingMiB, PrivateMiB = resources.PrivateMiB,
                ManagedMiB = GC.GetTotalMemory(false) / 1048576.0, CpuOneCorePercent = oneCore,
                CpuMachinePercent = oneCore / machineProcessors, Handles = resources.Handles,
                CpuAverageOneCorePercent = cpuMeter.AveragePercent, CpuAverageMachinePercent = cpuMeter.AveragePercent / machineProcessors,
                CpuIntervalSeconds = cpuMeter.IntervalSeconds, CpuAverageSeconds = cpuMeter.AverageSeconds, CpuTotalSeconds = resources.CpuSeconds,
                Threads = engine == null ? previewThreads : engine.KnownThreads, WindowVisible = Volatile.Read(ref visible) != 0,
                UiHeartbeatAgeSeconds = elapsed < suspendGraceUntil ? 0 : Age(Interlocked.Read(ref uiBeat)), EngineRequired = engine != null,
                EngineAlive = engine == null || engine.Alive, EngineHeartbeatAgeSeconds = engine == null || elapsed < suspendGraceUntil ? 0 : Age(engine.Heartbeat),
                EngineErrors = engine == null ? 0 : engine.ConsecutiveErrors,
                AppAffinityError = engine != null && engine.AppAffinity != null && engine.AppAffinity.Error
            };
            lock (gate) current = sample;
            // Keep a time-based health history: manual refresh must not evict the
            // five-minute leak-detection window or multiply policy observations.
            if (elapsed - historyElapsed >= 5)
            {
                historyElapsed = elapsed;
                lock (gate) { history.Enqueue(sample); while (history.Count > 180) history.Dequeue(); }
                string reason = policy.Observe(sample);
                if (reason != null) { Fatal(reason, null); return; }
            }
            if (Interlocked.CompareExchange(ref pendingBeat, 1, 0) == 0 && !dispatcher.HasShutdownStarted)
                dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate { Interlocked.Exchange(ref uiBeat, Stopwatch.GetTimestamp()); Interlocked.Exchange(ref pendingBeat, 0); }));
            if (Sampled != null) Sampled();
        }
        internal string ManualReport()
        {
            LastReport = reports.Write("user-request", null, History, context(), false);
            return LastReport;
        }
        internal void Fatal(string reason, Exception error)
        {
            if (Volatile.Read(ref disposed) != 0 || Interlocked.CompareExchange(ref fatal, 1, 0) != 0) return;
            finish.Set();
            if (armStop != null) armStop();
            if (engine != null) engine.RequestStop();
            try { LastReport = reports.Write(reason, error, History, context(), true); }
            catch (Exception writeError)
            {
                log.Write(L10n.T("Не удалось записать баг-репорт: ") + writeError.Message);
                try
                {
                    LastReport = new BugReports(reports.FallbackDataDirectory, log).Write(reason, error, History, new { Fallback = true }, true);
                }
                catch (Exception)
                {
                    try { LastReport = new BugReports(reports.EmergencyDataDirectory, log).Write(reason, error, History, new { EmergencyFallback = true }, true); }
                    catch (Exception) { }
                }
            }
            log.Write(L10n.T("Аварийное завершение: ") + reason + L10n.T(" · отчёт: ") + LastReport);
            stop(reason);
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            finish.Set();
            if (monitor.IsAlive && Thread.CurrentThread != monitor) monitor.Join(3000);
            if (!monitor.IsAlive) finish.Dispose();
        }
    }
}
