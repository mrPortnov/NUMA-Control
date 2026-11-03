using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace NumaControl
{
    internal static partial class Tests
    {
        private static int passed;
        private static readonly List<object> results = new List<object>();
        private static string root;
        private static Configuration SampleConfiguration(Topology t)
        {
            Configuration c = new Configuration(); string mask = t.Supported ? t.RecommendedMask().ToString("X") : "1";
            c.Games.Add(new GameProfile { Name = "Counter-Strike 2", ProcessName = "cs2", LaunchTarget = "steam://rungameid/730", Enabled = t.Supported, Mask = mask, Color = "#FFB66C", HighPriority = false });
            c.Games.Add(new GameProfile { Name = "Dota 2", ProcessName = "dota2", LaunchTarget = "steam://rungameid/570", Enabled = t.Supported, Mask = mask, Color = "#F07E87", HighPriority = false });
            c.Games.Add(new GameProfile { Name = "Warface", ProcessName = "Game", ExePath = @"D:\AstrumPlay\Warface\Bin64Release\Game.exe", LaunchTarget = "astrumplay://play/0.1177", Enabled = t.Supported, Mask = mask, Color = "#72CCB0", HighPriority = false });
            return c;
        }
        private static void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL: " + name);
            passed++; results.Add(new { Name = name, Passed = true }); Console.WriteLine("PASS " + name);
        }
        private static void Reject(Action action, string name)
        {
            bool rejected = false;
            try { action(); } catch (Exception) { rejected = true; }
            Check(rejected, name);
        }
        private static bool Until(Func<bool> condition, int ms)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < ms) { if (condition()) return true; Thread.Sleep(25); }
            return condition();
        }
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--profile-ui") return ProfileUi(args[1]);
            if (args.Length > 0 && args[0] == "--monitor-software-visible")
            {
                System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
                return Monitor(args[1], args[2], true);
            }
            if (args.Length > 0 && (args[0] == "--monitor" || args[0] == "--monitor-visible")) return Monitor(args[1], args[2], args[0] == "--monitor-visible");
            if (args.Length > 0 && args[0] == "--interactive") return Interactive(args[1]);
            if (args.Length > 0 && args[0] == "--fault") return FaultHost(args[1], args[2]);
            root = args[0]; Directory.CreateDirectory(root);
            try
            {
                Topology topology = Topology.Detect();
                Check(topology.Supported, "host topology is supported");
                Check(Topology.Bits(topology.AllMask) == topology.Count, "topology count and active mask agree");
                Check(topology.Cores.Aggregate(0UL, (m, c) => m | c.Mask) == topology.AllMask, "physical cores cover active CPUs");
                for (int n = 1; n <= 64; n++) Check(Topology.Bits(Topology.FullMask(n)) == n, "full mask / " + n + " CPUs");
                Check(Topology.FullMask(64) == ulong.MaxValue && Topology.FirstBit(1UL << 63) == 63, "unsigned CPU 63 boundary");
                Reject(delegate { Topology.FullMask(0); }, "zero CPUs rejected");
                Reject(delegate { Topology.FullMask(65); }, "65 CPUs rejected");
                Check(Topology.Bits(topology.OnePerCore()) == topology.Cores.Count, "one logical CPU per physical core");
                if (topology.Cores.Count > 2) Check((topology.WithoutEdgeCores() & (topology.Cores.First().Mask | topology.Cores.Last().Mask)) == 0, "physical edge preset excludes whole SMT cores");
                ValidateAndPersist(topology);
                HealthChecks();
                CpuAndNativeChecks();
                Release13Checks();
                ExtendedFunctional(topology);
                SelfAffinityChecks(topology, args[1]);
                ShortcutsAndLegacy();
                Channels();
                AffinityIntegration(topology, args[1]);
                Release15Functional(topology, args[1]);
                XamlSmoke();
                string releasePath = Path.GetFullPath(Path.Combine(root, "..", "..", "..", "NUMA-Control.exe"));
                string releaseHash;
                using (var sha = System.Security.Cryptography.SHA256.Create()) using (FileStream release = File.OpenRead(releasePath)) releaseHash = BitConverter.ToString(sha.ComputeHash(release)).Replace("-", "");
                File.WriteAllText(Path.Combine(root, "test-results.json"), ProfileStore.Serializer().Serialize(new { Date = DateTime.Now.ToString("o"), Version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(), ReleaseSha256 = releaseHash, Passed = passed, Tests = results }), Encoding.UTF8);
                Console.WriteLine("ALL PASSED: " + passed); return 0;
            }
            catch (Exception e)
            {
                File.WriteAllText(Path.Combine(root, "failure.txt"), e.ToString()); Console.Error.WriteLine(e); return 1;
            }
        }
        private static int Monitor(string directory, string identity, bool visible)
        {
            Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            using (AppController controller = new AppController(app, false, directory, new AppEnvironment(directory) { ActivateWindows = visible }))
            using (InstanceChannel channel = new InstanceChannel("-measure-" + identity, command => app.Dispatcher.BeginInvoke(new Action(delegate
            {
                if (command == "show") controller.ShowWindow();
                else if (command == "launch hide") foreach (Window w in app.Windows.Cast<Window>().ToArray()) w.Close();
                else if (command == "launch settings") { controller.ShowWindow(); Press(MainWindow(app), "SettingsNav"); }
                else if (command == "launch diagnostics") { controller.ShowWindow(); Press(MainWindow(app), "DiagnosticsNav"); }
                else if (command == "launch report") controller.Health.ManualReport();
                else if (command == "launch main") { controller.ShowWindow(); Press(MainWindow(app), "LibraryNav"); }
                else if (command.StartsWith("launch theme ", StringComparison.Ordinal))
                {
                    string theme = command.Substring("launch theme ".Length); int index = Array.IndexOf(Themes.Ids, theme); if(index < 0) throw new ArgumentException(theme);
                    controller.ShowWindow(); Press(MainWindow(app), "SettingsNav"); Control<ComboBox>(MainWindow(app), "ThemeSelector").SelectedIndex = index;
                    File.WriteAllText(Path.Combine(directory,"theme-ready.txt"),theme);
                }
                else if (command == "launch cpu-history") File.WriteAllText(Path.Combine(directory, "cpu-history.json"), ProfileStore.Serializer().Serialize(controller.Health.History));
                else if (command == "launch cpu-burst") StartCpuBurst();
                else if (command == "launch refresh-burst")
                {
                    controller.ShowWindow(); Press(MainWindow(app), "DiagnosticsNav");
                    Stopwatch burst = Stopwatch.StartNew(); var refresh = new System.Windows.Threading.DispatcherTimer(); refresh.Interval = TimeSpan.FromMilliseconds(100);
                    refresh.Tick += delegate { if (burst.Elapsed.TotalSeconds >= 10) refresh.Stop(); else Press(MainWindow(app), "RefreshDiagnostics"); }; refresh.Start();
                }
                else if (command == "launch stop") app.Shutdown();
            }))))
            {
                controller.Start(); channel.Start();
                File.WriteAllText(Path.Combine(directory, "ready.txt"), channel.PipeName);
                app.Run();
            }
            return Environment.ExitCode;
        }
        private static void StartCpuBurst()
        {
            new Thread(delegate()
            {
                Stopwatch total = Stopwatch.StartNew();
                while (total.Elapsed.TotalSeconds < 12)
                {
                    Stopwatch slice = Stopwatch.StartNew(); while (slice.ElapsedMilliseconds < 60) Thread.SpinWait(100);
                    int remaining = 100 - (int)slice.ElapsedMilliseconds; if (remaining > 0) Thread.Sleep(remaining);
                }
            }) { IsBackground = true, Name = "Known 60 percent CPU fixture" }.Start();
        }
        private static void ValidateAndPersist(Topology t)
        {
            Configuration c = SampleConfiguration(t); c.Validate(t, true);
            Check(Configuration.Defaults(t).Games.Count == 0 && c.Games[2].ExePath.EndsWith("Game.exe"), "empty production defaults and explicit test fixtures");
            Check(c.Games[0].Matches(@"C:\Games\CS2.exe") && !c.Games[0].Matches(@"C:\Games\unrelated.exe"), "name-only profile verifies live image name against PID reuse");
            ProfileStore store = new ProfileStore(Path.Combine(root, "data")); store.Save(c);
            Configuration round = store.Load(t);
            Check(round.Games[0].Id == c.Games[0].Id && round.Games[0].CpuMask == c.Games[0].CpuMask, "JSON round trip preserves IDs and unsigned mask");
            c.Paused = true; c.Games[0].Name = "Тест игры Юникод"; store.Save(c);
            Check(store.Load(t).Games[0].Name == c.Games[0].Name && store.Load(t).Paused, "atomic replacement preserves Unicode and pause");
            Check(File.Exists(store.FilePath + ".bak") && !ProfileStore.Read(store.FilePath + ".bak", t, true).Paused, "previous valid config kept as backup");
            Check(!Directory.GetFiles(store.DirectoryPath, "*.tmp").Any(), "atomic save leaves no temporary files");
            Configuration bad = c.Copy(); bad.Games[0].Mask = "0"; Reject(delegate { bad.Validate(t, true); }, "empty affinity mask rejected");
            bad.Games[0].Mask = "not hex"; Reject(delegate { bad.Validate(t, true); }, "non-hex mask rejected");
            bad = c.Copy(); bad.Games.Add(bad.Games[0].Copy()); Reject(delegate { bad.Validate(t, true); }, "duplicate ID rejected");
            bad = c.Copy(); GameProfile overlap = bad.Games[0].Copy(); overlap.Id = Guid.NewGuid().ToString("N"); bad.Games.Add(overlap); Reject(delegate { bad.Validate(t, true); }, "overlapping process rules rejected");
            bad = c.Copy(); bad.Version = 2; Reject(delegate { bad.Validate(t, true); }, "unsupported schema rejected");
            bad = c.Copy(); bad.Games[0].LaunchTarget = "cmd.exe"; Reject(delegate { bad.Validate(t, true); }, "relative launch command rejected");
            bad.Games[0].LaunchTarget = "https://example.invalid"; Reject(delegate { bad.Validate(t, true); }, "unsupported URL scheme rejected");
            bad = c.Copy(); bad.Games[0].ExePath = @"C:\Games\different.exe"; Reject(delegate { bad.Validate(t, true); }, "path and process mismatch rejected");
            bad = c.Copy(); bad.IdleIntervalMs = 1; Reject(delegate { bad.Validate(t, true); }, "busy-loop interval rejected");
            if (t.Count < 64) { bad = c.Copy(); bad.Games[0].Mask = (1UL << 63).ToString("X"); Reject(delegate { bad.Validate(t, true); }, "import with unavailable CPUs rejected"); bad.Validate(t, false); Check(true, "stored incompatible mask can be opened for repair"); }
            Check(Configuration.IsGameUri("steam://rungameid/730") && Configuration.IsGameUri("astrumplay://play/0.1177") && !Configuration.IsGameUri("steam://rungameid/730 extra"), "launcher URI validation is exact");
            File.WriteAllText(store.FilePath, "{broken json"); Reject(delegate { store.Load(t); }, "corrupt JSON reported without silent overwrite");
            Check(File.ReadAllText(store.FilePath) == "{broken json", "corrupt source preserved");
            Check(ProfileStore.Read(store.FilePath + ".bak", t, true).Games.Count == 3, "valid backup remains recoverable");
            store.PreserveRecoveryFiles();
            Check(Directory.GetFiles(store.DirectoryPath, "profiles.corrupt-*.json").Length == 1 && Directory.GetFiles(store.DirectoryPath, "profiles.recovery-*.json").Length == 1, "corrupt config and valid backup are preserved separately before recovery");
        }
        private static void ShortcutsAndLegacy()
        {
            string path = Path.Combine(root, "Startup test.lnk");
            ShellLinks.Autostart(true, path);
            Check(File.Exists(path) && string.Equals(ShellLinks.Target(path), ShellLinks.Executable, StringComparison.OrdinalIgnoreCase), "autostart link uses direct EXE via Windows Shell");
            object link = new ShellLinkObject();
            try
            {
                ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Load(path, 0);
                StringBuilder arguments = new StringBuilder(512); ((IShellLinkW)link).GetArguments(arguments, arguments.Capacity);
                Check(arguments.ToString() == "--tray", "autostart link starts in tray");
            }
            finally { Marshal.FinalReleaseComObject(link); }
            ShellLinks.Autostart(false, path); Check(!File.Exists(path), "autostart link removal is isolated");
            GameProfile game = new GameProfile { Name = "Игра: тест/Юникод?" };
            string desktop = Path.Combine(root, "desktop"); Directory.CreateDirectory(desktop);
            string first = ShellLinks.Desktop(game, desktop), second = ShellLinks.Desktop(game, desktop);
            Check(first == second && File.Exists(first), "desktop shortcut sanitizes names and updates existing game link");
            GameProfile other = new GameProfile { Name = game.Name };
            string otherLink = ShellLinks.Desktop(other, desktop);
            Check(otherLink != first && File.Exists(first) && File.Exists(otherLink), "same title with different ID never overwrites other profile shortcut");
            LegacyKit kit = new LegacyKit { Name = "Fixture", Prefix = "NumaTest-" + Guid.NewGuid().ToString("N"), ProcessName = "fixture", ExeName = "LegacyFixture.exe", ShortcutName = "Fixture", ShortcutOverride = Path.Combine(root, "Legacy fixture.lnk") };
            using (EventWaitHandle ready = new EventWaitHandle(true, EventResetMode.ManualReset, kit.Identity + "-Ready"))
            using (EventWaitHandle stop = new EventWaitHandle(false, EventResetMode.ManualReset, kit.Identity + "-Stop"))
            {
                Check(kit.Running, "legacy ready signal detected");
                ShellLinks.Write(kit.ShortcutPath, Path.Combine(root, kit.ExeName), "--watch", "fixture");
                string backup = Path.Combine(root, "legacy-backup"); kit.Retire(backup);
                Check(stop.WaitOne(0) && !File.Exists(kit.ShortcutPath) && Directory.GetFiles(backup).Length == 1, "migration signals stop and backs up known shortcut");
                ShellLinks.Write(kit.ShortcutPath, ShellLinks.Executable, "", "unknown target");
                Reject(delegate { kit.Retire(backup); }, "migration refuses unknown shortcut target");
                Check(File.Exists(kit.ShortcutPath), "unknown shortcut preserved");
            }
            Check(!kit.Running, "stopped legacy watcher detected");
        }
        private static void Channels()
        {
            string suffix = "-tests-" + Guid.NewGuid().ToString("N");
            string received = null;
            using (AutoResetEvent ready = new AutoResetEvent(false))
            using (InstanceChannel primary = new InstanceChannel(suffix, command => { received = command; ready.Set(); }))
            using (InstanceChannel second = new InstanceChannel(suffix, command => { }))
            {
                Check(primary.Primary && !second.Primary, "single instance mutex per user and session"); primary.Start();
                second.Send("show"); Check(ready.WaitOne(3000) && received == "show", "second instance forwards show command");
                string command = "launch " + Guid.NewGuid().ToString("N"); second.Send(command); Check(ready.WaitOne(3000) && received == command, "desktop launch forwarded to existing instance");
                for (int i = 0; i < 20; i++)
                {
                    command = "launch " + Guid.NewGuid().ToString("N"); second.Send(command);
                    Check(ready.WaitOne(3000) && received == command, "successive IPC command / " + i);
                }
                using (var partial = new System.IO.Pipes.NamedPipeClientStream(".", primary.PipeName, System.IO.Pipes.PipeDirection.InOut))
                {
                    partial.Connect(3000); byte[] prefix = Encoding.UTF8.GetBytes("sho"); partial.Write(prefix, 0, prefix.Length); partial.Flush();
                    Thread.Sleep(40); partial.WriteByte((byte)'w'); partial.WriteByte((byte)'\n'); partial.Flush();
                    Check(ready.WaitOne(3000) && received == "show", "fragmented IPC command reassembled"); partial.ReadByte();
                }
                second.Send("arbitrary command"); Check(!ready.WaitOne(150), "unknown IPC command ignored");
            }
            Check(true, "IPC listener disposes while waiting for connection");
        }
        private static void AffinityIntegration(Topology t, string fixturePath)
        {
            string processName = Path.GetFileNameWithoutExtension(fixturePath);
            Configuration c = new Configuration();
            GameProfile g = new GameProfile { Name = "Isolated affinity fixture", ProcessName = processName, ExePath = fixturePath, Mask = (t.AllMask & ~(1UL << Topology.FirstBit(t.AllMask))).ToString("X"), Enabled = true };
            if (g.CpuMask == 0) g.Mask = t.AllMask.ToString("X");
            c.Games.Add(g); c.Validate(t, true);
            using (Process fixture = Process.Start(new ProcessStartInfo(fixturePath) { UseShellExecute = false, CreateNoWindow = true }))
            using (WatchEngine engine = new WatchEngine(c, t, new AppLog(Path.Combine(root, "logs"))))
            {
                try
                {
                    Check(Until(delegate { try { using (var h = Native.Open(fixture.Id, false)) return Native.Image(h) == fixturePath; } catch { return false; } }, 3000), "fixture starts and exposes image identity");
                    var first = engine.Scan(c)[g.Id]; Check(first.Applied == 1 && !first.Error && first.Pids.Contains(fixture.Id), "watcher applies selected affinity to exact target");
                    using (var h = Native.Open(fixture.Id, true))
                    {
                        Check(Native.ReadMask(h) == g.CpuMask, "Windows confirms actual process affinity");
                        Check(!Native.Apply(h, g.CpuMask), "unchanged affinity avoids write"); Native.Apply(h, t.AllMask);
                    }
                    engine.Scan(c); using (var h = Native.Open(fixture.Id, false)) Check(Native.ReadMask(h) == g.CpuMask, "external affinity reset is restored");
                    Configuration wrong = c.Copy(); wrong.Games[0].ExePath = Path.Combine(Path.GetDirectoryName(fixturePath), "other", Path.GetFileName(fixturePath));
                    using (var h = Native.Open(fixture.Id, true)) Native.Apply(h, t.AllMask);
                    Check(engine.Scan(wrong)[g.Id].Running == 0, "same process name with different path ignored");
                    using (var h = Native.Open(fixture.Id, false)) Check(Native.ReadMask(h) == t.AllMask, "wrong path affinity remains unchanged");
                    Configuration disabled = c.Copy(); disabled.Games[0].Enabled = false; engine.Scan(disabled);
                    using (var h = Native.Open(fixture.Id, false)) Check(Native.ReadMask(h) == t.AllMask, "disabled profile does not modify process");
                    Configuration paused = c.Copy(); paused.Paused = true; engine.Scan(paused);
                    using (var h = Native.Open(fixture.Id, false)) Check(Native.ReadMask(h) == t.AllMask, "global pause does not modify process");
                    engine.Start(); Check(Until(delegate { return engine.Snapshot().ContainsKey(g.Id) && engine.Snapshot()[g.Id].Applied == 1; }, 4000), "background engine discovers live process");
                    Configuration changed = c.Copy(); changed.Games[0].Mask = t.AllMask.ToString("X"); engine.Update(changed);
                    Check(Until(delegate { using (var h = Native.Open(fixture.Id, false)) return Native.ReadMask(h) == t.AllMask; }, 3000), "live profile change wakes engine and applies new mask");
                    engine.Update(paused); Check(Until(delegate { return engine.Snapshot()[g.Id].State == "Общая пауза"; }, 3000), "pause state visible in watcher snapshot");
                    Stopwatch latency = Stopwatch.StartNew(); engine.Update(c);
                    Check(Until(delegate { using (var h = Native.Open(fixture.Id, false)) return Native.ReadMask(h) == g.CpuMask; }, 3000), "resume interrupts long paused wait");
                    Console.WriteLine("Resume latency: " + latency.ElapsedMilliseconds + " ms");
                    fixture.Kill(); fixture.WaitForExit(); engine.Refresh();
                    Check(Until(delegate { return engine.Snapshot()[g.Id].State == "Ожидание игры"; }, 3000), "exited process changes status back to waiting");
                    using (Process again = Process.Start(new ProcessStartInfo(fixturePath) { UseShellExecute = false, CreateNoWindow = true }))
                    {
                        try { engine.Refresh(); Check(Until(delegate { using (var h = Native.Open(again.Id, false)) return Native.ReadMask(h) == g.CpuMask; }, 4000), "game restart receives same rule"); }
                        finally { if (!again.HasExited) { again.Kill(); again.WaitForExit(); } }
                    }
                }
                finally { if (!fixture.HasExited) { fixture.Kill(); fixture.WaitForExit(); } }
            }
        }
        private static void XamlSmoke()
        {
            Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.Resources.MergedDictionaries.Add(Ui.Load<ResourceDictionary>("Theme.xaml"));
            Window window = Ui.Load<Window>("MainWindow.xaml");
            foreach (string name in new[] { "GameList", "AddGame", "SettingsNav", "LogNav", "ProfilePanel", "EnabledToggle", "CpuMap", "SaveMask", "SettingsBody", "LogText", "LegacyBanner", "LiveState" }) Check(window.FindName(name) != null, "XAML named element " + name);
            window.Measure(new Size(1220, 870)); window.Arrange(new Rect(0, 0, 1220, 870)); window.UpdateLayout();
            FrameworkElement content = (FrameworkElement)window.Content;
            content.Measure(new Size(1220, 870)); content.Arrange(new Rect(0, 0, 1220, 870)); content.UpdateLayout();
            Check(content.ActualWidth > 1000, "window resources, styles and templates instantiate");
            TextBox input = new TextBox { Style = (Style)app.FindResource(typeof(TextBox)), Width = 360, Height = 40, Text = "Visible profile text", FontSize = 14 };
            input.ApplyTemplate();
            input.Measure(new Size(360, 40)); input.Arrange(new Rect(0, 0, 360, 40)); input.UpdateLayout();
            ScrollViewer host = (ScrollViewer)input.Template.FindName("PART_ContentHost", input);
            Check(host != null && host.ViewportHeight >= 16, "single-line field has visible text viewport after padding");
            ProfileEditor editor = new ProfileEditor(null, null, Topology.Detect(), new Configuration());
            Check(editor.Background == app.FindResource("Bg"), "derived editor window inherits dark theme explicitly"); editor.Close();
            Check(true, "profile editor constructs with theme");
            string lifecyclePath = Path.Combine(root, "ui-lifecycle");
            Configuration paused = SampleConfiguration(Topology.Detect()); paused.Paused = true;
            new ProfileStore(lifecyclePath).Save(paused);
            using (AppController controller = new AppController(app, false, lifecyclePath))
            {
                controller.Start(); controller.ShowWindow();
                Window first = app.Windows.Cast<Window>().Single(w => w.IsVisible && w.Title.EndsWith("— тест"));
                string selectedName = ((TextBlock)first.FindName("SelectedName")).Text;
                Check(first.IsVisible, "isolated application opens its actual main window");
                first.Width = 1440; first.Height = 960; first.UpdateLayout();
                FrameworkElement surface = (FrameworkElement)first.Content;
                ((Grid)surface).Background = (System.Windows.Media.Brush)app.FindResource("Bg");
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                var backdrop = new System.Windows.Media.DrawingVisual();
                using (var drawing = backdrop.RenderOpen()) drawing.DrawRectangle((System.Windows.Media.Brush)app.FindResource("Bg"), null, new Rect(0, 0, surface.ActualWidth, surface.ActualHeight));
                bitmap.Render(backdrop);
                bitmap.Render(surface);
                var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using (FileStream image = File.Create(Path.Combine(root, "interface-preview.png"))) png.Save(image);
                first.Close(); Check(!first.IsVisible && app.Windows.Cast<Window>().All(w => !w.IsVisible), "closing window keeps background application alive in tray");
                controller.ShowWindow(); Window second = app.Windows.Cast<Window>().Single(w => w.IsVisible && w.Title.EndsWith("— тест"));
                Check(!object.ReferenceEquals(first, second) && ((TextBlock)second.FindName("SelectedName")).Text == selectedName, "tray reopen recreates released window and preserves selected profile");
                second.Close(); Check(Until(delegate { return controller.Engine.Snapshot().Values.All(s => s.State == "Общая пауза"); }, 2000), "engine remains usable after repeated window closure");
            }
            UiFlows(app);
            if (!app.Dispatcher.HasShutdownStarted) { window.Close(); app.Shutdown(); }
        }
    }
}
