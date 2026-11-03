using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace NumaControl
{
    internal static partial class Tests
    {
        private static int ProfileUi(string directory)
        {
            Directory.CreateDirectory(directory);
            Configuration config = SampleConfiguration(Topology.Detect()); config.Paused = true; new ProfileStore(directory).Save(config);
            Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            using (AppController controller = new AppController(app, false, directory, new AppEnvironment(directory) { ActivateWindows = true }))
            {
                controller.Start(); controller.ShowWindow(); Window window = MainWindow(app); object content = window.Content;
                string stage = "full"; object gate = new object(); var counts = new System.Collections.Generic.Dictionary<string, int>();
                Action<string> count = key => { lock (gate) { key = stage + ":" + key; int n; counts.TryGetValue(key, out n); counts[key] = n + 1; } };
                var field = typeof(System.Windows.Threading.DispatcherOperation).GetField("_method", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                app.Dispatcher.Hooks.OperationPosted += (sender, e) => { Delegate method = field == null ? null : field.GetValue(e.Operation) as Delegate; count("operation:" + e.Operation.Priority + ":" + (method == null ? "unknown" : method.Method.DeclaringType.FullName + "." + method.Method.Name)); };
                window.LayoutUpdated += (sender, e) => count("layout");
                System.Windows.Interop.HwndSource source = System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(window).Handle);
                source.AddHook(delegate(IntPtr hwnd, int msg, IntPtr wparam, IntPtr lparam, ref bool handled) { count("message:" + msg.ToString("X")); return IntPtr.Zero; });
                var samples = new System.Collections.Generic.List<object>(); Stopwatch watch = Stopwatch.StartNew(); double previousCpu = OwnResources.Read().CpuSeconds, previousTime = 0; int tick = 0;
                using (Timer timer = new Timer(delegate
                {
                    double time = watch.Elapsed.TotalSeconds, cpu = OwnResources.Read().CpuSeconds;
                    samples.Add(new { Stage = stage, Time = time, CorePercent = 100 * (cpu - previousCpu) / (time - previousTime) }); previousCpu = cpu; previousTime = time;
                    int n = Interlocked.Increment(ref tick);
                    if (n == 10) app.Dispatcher.BeginInvoke(new Action(() => { window.Content = null; stage = "empty"; }));
                    if (n == 20) app.Dispatcher.BeginInvoke(new Action(() => { window.Content = content; stage = "restored"; }));
                    if (n == 30) app.Dispatcher.BeginInvoke(new Action(() => { lock (gate) File.WriteAllText(Path.Combine(directory, "ui-profile.json"), ProfileStore.Serializer().Serialize(new { Counts = counts, Samples = samples, Tier = System.Windows.Media.RenderCapability.Tier, Mode = System.Windows.Media.RenderOptions.ProcessRenderMode.ToString() })); app.Shutdown(); }));
                }, null, 1000, 1000)) app.Run();
            }
            return 0;
        }
        private static int Interactive(string directory)
        {
            Directory.CreateDirectory(directory);
            if (!File.Exists(Path.Combine(directory, "profiles.json")))
            {
                Configuration c = SampleConfiguration(Topology.Detect()); c.Paused = true; foreach (GameProfile g in c.Games) g.Enabled = false;
                new ProfileStore(directory).Save(c);
            }
            Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            using (AppController controller = new AppController(app, false, directory, new AppEnvironment(directory) { ActivateWindows = true }))
            using (ExceptionHooks hooks = new ExceptionHooks(app, controller.Critical))
            {
                controller.Start(); controller.ShowWindow(); app.Run();
            }
            return Environment.ExitCode;
        }
        private static int FaultHost(string directory, string mode)
        {
            Directory.CreateDirectory(directory);
            new ProfileStore(directory).Save(new Configuration { Paused = true });
            Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            AppEnvironment env = new AppEnvironment(directory);
            if (mode == "memory") env.DiagnosticLimits = new HealthLimits { PrivateMiB = 1, SustainedSeconds = 0 };
            if (mode == "ui-hang") env.DiagnosticLimits = new HealthLimits { UiTimeoutSeconds = 2 };
            using (AppController controller = new AppController(app, false, directory, env))
            using (ExceptionHooks hooks = new ExceptionHooks(app, controller.Critical))
            {
                controller.Start();
                app.Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (mode == "ui-exception") throw new NullReferenceException("Injected UI fault for isolated test");
                    if (mode == "ui-hang") Thread.Sleep(60000);
                    if (mode == "engine-stop") controller.Engine.RequestStop();
                    if (mode == "background-exception") new Thread(delegate() { throw new InvalidOperationException("Injected background thread fault"); }) { IsBackground = true }.Start();
                    if (mode == "task-exception") { StartUnobservedTask(); new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(1), System.Windows.Threading.DispatcherPriority.Background, delegate { GC.Collect(); GC.WaitForPendingFinalizers(); }, app.Dispatcher).Start(); }
                }));
                app.Run();
            }
            return Environment.ExitCode;
        }
        private static void StartUnobservedTask() { System.Threading.Tasks.Task.Run(new Action(delegate { throw new InvalidOperationException("Injected unobserved task fault"); })); }
    }
}
