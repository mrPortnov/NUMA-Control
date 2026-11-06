using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace NumaControl
{
    internal sealed class ScriptedInteraction : Interaction
    {
        internal readonly Queue<MessageBoxResult> Answers = new Queue<MessageBoxResult>();
        internal readonly Queue<string> OpenPaths = new Queue<string>(), SavePaths = new Queue<string>();
        internal readonly Queue<Action<ProfileEditor>> Editors = new Queue<Action<ProfileEditor>>();
        internal readonly Queue<Action<ProcessPicker>> Pickers = new Queue<Action<ProcessPicker>>();
        internal readonly List<string> Messages = new List<string>();
        internal string Revealed;
        internal override MessageBoxResult Ask(Window owner, string text, string title, MessageBoxButton buttons, MessageBoxImage image)
        {
            Messages.Add(title + " | " + text);
            if (buttons == MessageBoxButton.OK) return MessageBoxResult.OK;
            if (Answers.Count == 0) throw new Exception("Unscripted confirmation: " + title);
            return Answers.Dequeue();
        }
        internal override string Open(Window owner, string title, string filter) { if (OpenPaths.Count == 0) throw new Exception("Unscripted file open: " + title); return OpenPaths.Dequeue(); }
        internal override string Save(Window owner, string title, string filter, string name) { if (SavePaths.Count == 0) throw new Exception("Unscripted file save: " + title); return SavePaths.Dequeue(); }
        internal override void Reveal(string directory) { Revealed = directory; }
        private bool? Drive<T>(T window, Action<T> action) where T : Window
        {
            Exception failure = null;
            window.Loaded += delegate { window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate { try { action(window); } catch (Exception e) { failure = e; window.DialogResult = false; } })); };
            bool? result = window.ShowDialog();
            if (failure != null) throw new Exception("Modal flow failed", failure);
            return result;
        }
        internal override bool? Edit(ProfileEditor editor) { if (Editors.Count == 0) throw new Exception("Unscripted editor"); return Drive(editor, Editors.Dequeue()); }
        internal override bool? Pick(ProcessPicker picker) { if (Pickers.Count == 0) throw new Exception("Unscripted picker"); return Drive(picker, Pickers.Dequeue()); }
    }
    internal static partial class Tests
    {
        private static ResourceSample Healthy(double seconds)
        {
            return new ResourceSample { TimeUtc = DateTime.UtcNow.ToString("o"), ElapsedSeconds = seconds, PrivateMiB = 80, WorkingMiB = 100, Handles = 800, Threads = 15, CpuOneCorePercent = 1, EngineAlive = true, EngineRequired = true };
        }
        private static void HealthChecks()
        {
            Func<HealthPolicy> policy = () => new HealthPolicy(new HealthLimits());
            HealthPolicy normal = policy();
            for (int t = 0; t <= 1200; t += 5) Check(normal.Observe(Healthy(t)) == null, "health accepts stable low resources / second " + t);
            foreach (string mode in new[] { "private", "working", "handles" })
            {
                HealthPolicy p = policy(); string reason = null;
                for (int t = 0; t <= 30; t += 5)
                {
                    ResourceSample s = Healthy(t);
                    if (mode == "private") s.PrivateMiB = 520;
                    else if (mode == "working") s.WorkingMiB = 780;
                    else s.Handles = 6100;
                    reason = p.Observe(s);
                    if (t < 30) Check(reason == null, "sustained " + mode + " does not trip early / " + t);
                }
                Check(reason == (mode == "private" ? "private-memory-limit" : mode == "working" ? "working-memory-limit" : "handle-limit"), "sustained " + mode + " limit trips at 30 seconds");
            }
            HealthPolicy spikes = policy();
            for (int t = 0; t <= 600; t += 5)
            {
                ResourceSample s = Healthy(t); if (t % 30 == 0) { s.PrivateMiB = 600; s.Handles = 7000; s.WorkingMiB = 900; s.CpuOneCorePercent = 100; }
                Check(spikes.Observe(s) == null, "isolated resource spikes recover / " + t);
            }
            ResourceSample fault = Healthy(0); fault.UiHeartbeatAgeSeconds = 46; Check(policy().Observe(fault) == "ui-unresponsive", "UI heartbeat timeout detected");
            fault = Healthy(0); fault.EngineHeartbeatAgeSeconds = 91; Check(policy().Observe(fault) == "engine-unresponsive", "engine heartbeat timeout detected");
            fault = Healthy(0); fault.EngineAlive = false; Check(policy().Observe(fault) == "engine-unresponsive", "unexpected worker exit detected");
            fault.EngineRequired = false; Check(policy().Observe(fault) == null, "preview does not require watcher thread");
            fault = Healthy(0); fault.EngineErrors = 5; Check(policy().Observe(fault) == "repeated-engine-errors", "consecutive scan failures detected");
            foreach (bool visible in new[] { false, true })
            {
                HealthPolicy p = policy(); string reason = null; int period = visible ? 120 : 60;
                for (int t = 0; t <= period; t += 5) { ResourceSample s = Healthy(t); s.WindowVisible = visible; s.CpuOneCorePercent = 100; reason = p.Observe(s); if (t < period) Check(reason == null, "CPU grace / visible=" + visible + " / " + t); }
                Check(reason == "sustained-high-cpu", "sustained CPU detected / visible=" + visible);
            }
            HealthPolicy leak = policy(); string leakReason = null;
            for (int t = 0; t <= 380; t += 5) { ResourceSample s = Healthy(t); s.PrivateMiB = 80 + t * 0.4; leakReason = leak.Observe(s); if (leakReason != null) break; }
            Check(leakReason == "suspected-memory-leak", "sustained memory growth detected after warmup and five-minute window");
            HealthPolicy handles = policy(); string handleReason = null;
            for (int t = 0; t <= 380; t += 5) { ResourceSample s = Healthy(t); s.Handles = 800 + t * 8; handleReason = handles.Observe(s); if (handleReason != null) break; }
            Check(handleReason == "suspected-handle-leak", "sustained handle growth detected");
            HealthPolicy lazyUi = policy();
            for (int t = 0; t <= 600; t += 5) { ResourceSample s = Healthy(t); s.WindowVisible = t >= 100; s.PrivateMiB = t >= 100 ? 180 : 50; Check(lazyUi.Observe(s) == null, "one-time WPF load is not a leak / " + t); }
            Reject(delegate { lazyUi.Observe(Healthy(1)); }, "health rejects backwards elapsed time");
            string directory = Path.Combine(root, "health"); AppLog log = new AppLog(directory); BugReports reports = new BugReports(directory, log);
            log.Write("local diagnostic fixture");
            for (int i = 0; i < 24; i++) reports.Write("manual-test", null, new[] { Healthy(i) }, new { Profiles = 0 }, false);
            Check(Directory.GetFiles(reports.DirectoryPath, "diagnostic-*.json").Length == 20, "report retention is bounded at 20 files");
            Check(!reports.PreviousFailure, "manual reports do not set failure marker");
            string report = reports.Write("fixture-failure", new InvalidOperationException("isolated fault"), new[] { Healthy(0) }, new { Profiles = 1 }, true);
            var json = ProfileStore.Serializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(report));
            Check(reports.PreviousFailure && (string)json["Kind"] == "fatal" && File.ReadAllText(report).Contains("isolated fault"), "fatal report contains exception and sets restart marker");
            Check(new FileInfo(report).Length < 1024 * 1024, "failure report stays below one MiB");
            reports.ClearFailure(); Check(!reports.PreviousFailure, "explicit recovery clears failure marker");
        }
        private static void ExtendedFunctional(Topology topology)
        {
            string directory = Path.Combine(root, "validation-extra"); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "profiles.json");
            foreach (string text in new[] { "{}", "null", "[]", "{\"Version\":1}", "{\"Version\":1,\"Games\":null,\"IdleIntervalMs\":2000,\"ActiveIntervalMs\":1000,\"Paused\":false}" })
            {
                File.WriteAllText(path, text); Reject(delegate { ProfileStore.Read(path, topology, true); }, "malformed or incomplete schema rejected: " + text);
            }
            File.WriteAllText(path, new string('x', 1024 * 1024 + 1)); Reject(delegate { ProfileStore.Read(path, topology, true); }, "oversized import rejected before parsing");
            foreach (string mask in new[] { "", " ", "-1", "0x3FFC", "10000000000000000", "0" })
            { Configuration bad = SampleConfiguration(topology); bad.Games[0].Mask = mask; Reject(delegate { bad.Validate(topology, true); }, "invalid mask rejected: " + mask); }
            foreach (string process in new[] { "cs2.exe", " cs2", "cs2 ", "..", ".", @"C:\game.exe", "game/name", "" })
            { Configuration bad = SampleConfiguration(topology); bad.Games[0].ProcessName = process; Reject(delegate { bad.Validate(topology, true); }, "invalid process name rejected: " + process); }
            Configuration empty = new Configuration(); empty.Validate(topology, true); new ProfileStore(directory).Save(empty); Check(new ProfileStore(directory).Load(topology).Games.Count == 0, "empty library persists and loads intentionally");
            Configuration separate = new Configuration(); separate.Games.Add(new GameProfile { Name = "A", ProcessName = "shared", ExePath = @"C:\Games\A\shared.exe", Mask = "1" }); separate.Games.Add(new GameProfile { Name = "B", ProcessName = "shared", ExePath = @"C:\Games\B\shared.exe", Mask = "1" }); separate.Validate(topology, true); Check(true, "same executable basename with distinct exact paths is allowed");
            string fixture = Directory.GetFiles(root, "NumaFixture*.exe").Single();
            GameProfile game = new GameProfile { Name = "Launcher fixture", ProcessName = Path.GetFileNameWithoutExtension(fixture), ExePath = fixture, LaunchTarget = fixture };
            try
            {
                GameLauncher.Launch(game); Check(Until(() => GameLauncher.Running(game), 3000), "direct game launch starts exact executable");
                GameLauncher.Launch(game); Process[] duplicates = Process.GetProcessesByName(game.ProcessName); try { Check(duplicates.Length == 1, "launching an existing game does not duplicate process"); } finally { foreach (Process p in duplicates) p.Dispose(); }
            }
            finally { foreach (Process p in Process.GetProcessesByName(game.ProcessName)) using (p) { if (!p.HasExited) { p.Kill(); p.WaitForExit(); } } }
            game.LaunchTarget = Path.Combine(root, "missing.exe"); Reject(() => GameLauncher.Launch(game), "missing launch file reported");
            game.LaunchTarget = ""; game.ExePath = ""; Reject(() => GameLauncher.Launch(game), "unset launch target reported");
            AppLog rotated = new AppLog(Path.Combine(root, "rotating-log")); Directory.CreateDirectory(Path.GetDirectoryName(rotated.PathName)); File.WriteAllText(rotated.PathName, new string('x', 1024 * 1024 + 1)); rotated.Write("after rotation");
            Check(File.Exists(rotated.PathName + ".previous") && rotated.Tail().Contains("after rotation"), "log rotates above one MiB and remains readable");
            for (int i = 0; i < 300; i++) rotated.Write("line " + i);
            Check(rotated.Tail().Split(new[] { Environment.NewLine }, StringSplitOptions.None).Length <= 250 && rotated.Tail().StartsWith(DateTime.Now.ToString("yyyy-MM-dd")), "log viewer bounds newest-first tail");
        }
        private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
        {
            yield return node;
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++) foreach (DependencyObject child in Descendants(VisualTreeHelper.GetChild(node, i))) yield return child;
        }
        private static T Control<T>(Window window, string name) where T : FrameworkElement
        {
            window.UpdateLayout(); T result = window.FindName(name) as T ?? Descendants(window).OfType<T>().FirstOrDefault(c => c.Name == name);
            if (result == null) throw new Exception("Missing control: " + name + " in " + window.Title);
            return result;
        }
        private static void Press(Window window, string name)
        {
            ButtonBase button = Control<ButtonBase>(window, name);
            if (!button.IsEnabled) throw new Exception("Disabled button: " + name);
            button.BringIntoView();
            typeof(ButtonBase).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(button, null);
            Pump();
        }
        private static void Pump()
        {
            DispatcherFrame frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
        private static Window MainWindow(Application app) { return app.Windows.Cast<Window>().Single(w => w.IsVisible && w.Title.EndsWith(L10n.T(" — тест"), StringComparison.Ordinal)); }
        private static void UiFlows(Application app)
        {
            string directory = Path.Combine(root, "full-ui"); Configuration initial = SampleConfiguration(Topology.Detect()); initial.Paused = true;
            new ProfileStore(directory).Save(initial);
            ScriptedInteraction interaction = new ScriptedInteraction();
            AppEnvironment environment = new AppEnvironment(directory) { Interaction = interaction, ExitOnFatal = false };
            string fixture = Directory.GetFiles(root, "NumaFixture*.exe").Single();
            using (AppController controller = new AppController(app, false, directory, environment))
            {
                controller.Start(); controller.ShowWindow(); Window w = MainWindow(app);
                Release13UiChecks(controller, w);
                Release14UiChecks(controller, w);
                string id = controller.Config.Games[0].Id;
                Press(w, "PresetAll"); Check(Control<TextBlock>(w, "MaskLabel").Text == "0x" + controller.Topology.AllMask.ToString("X"), "UI all-CPU preset updates draft");
                Press(w, "SaveMask"); Check(controller.Config.Games[0].CpuMask == controller.Topology.AllMask && !Control<Button>(w, "SaveMask").IsEnabled, "UI save commits draft and clears dirty state");
                Check(w.FindName("PresetLegacy") == null, "UI has no obsolete legacy preset");
                foreach (string preset in new[] { "PresetEdges", "PresetPhysical" })
                { Press(w, preset); ulong expected = preset == "PresetEdges" ? controller.Topology.WithoutEdgeCores() : controller.Topology.OnePerCore(); Check(Control<TextBlock>(w, "MaskLabel").Text == "0x" + expected.ToString("X"), "UI preset maps to intended CPUs: " + preset); if (Control<Button>(w, "SaveMask").IsEnabled) Press(w, "SaveMask"); }
                ToggleButton[] cpus = Descendants(Control<WrapPanel>(w, "CpuMap")).OfType<ToggleButton>().ToArray();
                foreach (ToggleButton cpu in cpus) if (cpu.IsChecked == true) typeof(ButtonBase).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(cpu, null);
                Check(!Control<Button>(w, "SaveMask").IsEnabled && Control<TextBlock>(w, "MaskLabel").Text == "0x0", "UI prevents saving an empty CPU selection");
                Press(w, "PresetAll");
                interaction.Answers.Enqueue(MessageBoxResult.Cancel); Press(w, "SettingsNav"); Check(Control<ScrollViewer>(w, "LibraryView").Visibility == Visibility.Visible, "cancel unsaved prompt stays on library");
                interaction.Answers.Enqueue(MessageBoxResult.No); Press(w, "SettingsNav"); Check(Control<ScrollViewer>(w, "SettingsView").Visibility == Visibility.Visible, "discard unsaved prompt navigates without saving");
                foreach (int ms in new[] { 5000, 1000, 2000 }) { Press(w, "Interval" + ms); Check(controller.Config.IdleIntervalMs == ms, "settings interval button persists " + ms); }
                Press(w, "AutostartToggle"); Check(File.Exists(environment.StartupPath) && ShellLinks.Target(environment.StartupPath) == ShellLinks.Executable, "UI autostart enables actual shortcut in isolated startup directory");
                Press(w, "AutostartToggle"); Check(!File.Exists(environment.StartupPath), "UI autostart disables isolated shortcut");
                string export = Path.Combine(directory, "export.json"); interaction.SavePaths.Enqueue(export); Press(w, "ExportProfiles"); Check(ProfileStore.Read(export, controller.Topology, true).Games.Count == 3, "UI export writes valid library");
                interaction.SavePaths.Enqueue(null); Press(w, "ExportProfiles"); Check(controller.Config.Games.Count == 3, "cancel export has no state change");
                interaction.OpenPaths.Enqueue(null); Press(w, "ImportProfiles"); Check(controller.Config.Games.Count == 3, "cancel import picker has no state change");
                interaction.OpenPaths.Enqueue(export); interaction.Answers.Enqueue(MessageBoxResult.Cancel); Press(w, "ImportProfiles"); Check(controller.Config.Games[0].Id == id, "cancel import confirmation preserves library");
                interaction.OpenPaths.Enqueue(export); interaction.Answers.Enqueue(MessageBoxResult.OK); Press(w, "ImportProfiles"); Check(controller.Config.Paused && controller.Config.Games[0].Id == id, "UI import replaces library and pauses observation");
                string corrupt = Path.Combine(directory, "invalid.json"); File.WriteAllText(corrupt, "{}"); int messages = interaction.Messages.Count; interaction.OpenPaths.Enqueue(corrupt); Press(w, "ImportProfiles"); Check(interaction.Messages.Count == messages + 1 && controller.Config.Games.Count == 3 && !controller.Health.IsFatal, "invalid import shows error without changing state or terminating");
                Press(w, "LibraryNav");
                Press(w, "EnabledToggle"); Check(!controller.Config.Games[0].Enabled, "watch toggle disables selected profile");
                Press(w, "EnabledToggle"); Check(controller.Config.Games[0].Enabled, "watch toggle enables selected profile");
                Press(w, "DesktopShortcut"); Check(Directory.GetFiles(environment.DesktopDirectory, "*.lnk").Length == 1, "UI desktop button creates isolated game shortcut");
                Press(w, "DesktopShortcut"); Check(Directory.GetFiles(environment.DesktopDirectory, "*.lnk").Length == 1, "UI desktop repeat updates same shortcut");
                Press(w, "PauseAll"); Check(!controller.Config.Paused, "global continue resumes observation"); Press(w, "PauseAll"); Check(controller.Config.Paused, "global pause stops observation");
                interaction.Editors.Enqueue(editor => { Press(editor, "SaveProfile"); Check(interaction.Messages.Last().Contains("название"), "empty editor validation explains missing name"); Press(editor, "CancelProfile"); });
                Press(w, "AddGame"); Check(controller.Config.Games.Count == 3, "cancel add leaves library intact");
                string launcherLink = Path.Combine(directory, "Game launcher.url");
                const string launcherUri = "uplay://launch/123/0";
                File.WriteAllText(launcherLink, "[InternetShortcut]\r\nURL=" + launcherUri + "\r\n");
                interaction.Editors.Enqueue(editor =>
                {
                    interaction.OpenPaths.Enqueue(fixture); Press(editor, "BrowseGameExe");
                    interaction.OpenPaths.Enqueue(launcherLink); Press(editor, "BrowseLauncherShortcut");
                    Check(Control<TextBox>(editor, "GamePathInput").Text == fixture && Control<TextBox>(editor, "LaunchTargetInput").Text == launcherUri,
                        "profile editor imports the launcher shortcut without changing the monitored EXE");
                    Press(editor, "CancelProfile");
                });
                Press(w, "AddGame");
                interaction.Editors.Enqueue(editor =>
                {
                    const string client = "FortniteClient-Win64-Shipping.exe";
                    string gamePath = Path.Combine(Path.GetDirectoryName(fixture), client);
                    interaction.OpenPaths.Enqueue(gamePath);
                    Press(editor, "BrowseGameExe");
                    Check(Control<TextBox>(editor, "GamePathInput").Text == gamePath &&
                        Control<TextBox>(editor, "ProcessNameInput").Text == Path.GetFileNameWithoutExtension(client) &&
                        Control<TextBox>(editor, "LaunchTargetInput").Text == "",
                        "the normal game editor leaves the Fortnite launch target automatic");
                    Press(editor, "CancelProfile");
                });
                Press(w, "AddGame");
                interaction.Editors.Enqueue(editor => { interaction.OpenPaths.Enqueue(fixture); Press(editor, "BrowseGameExe"); Check(Control<TextBox>(editor, "GamePathInput").Text == fixture, "browse game fills exact path"); Check(Control<TextBox>(editor, "ProcessNameInput").Text == Path.GetFileNameWithoutExtension(fixture), "browse game derives process name"); Control<TextBox>(editor, "GameNameInput").Text = "Тест игрового профиля"; interaction.OpenPaths.Enqueue(fixture); Press(editor, "BrowseLaunchExe"); Press(editor, "SaveProfile"); });
                Press(w, "AddGame"); Check(controller.Config.Games.Count == 4 && Control<TextBlock>(w, "SelectedName").Text == "Тест игрового профиля", "add via EXE creates and selects persisted profile");
                string fixtureId = controller.Config.Games.Last().Id;
                ulong fixtureMask = controller.Topology.Cores[0].Mask;
                foreach (ToggleButton cpu in Descendants(Control<WrapPanel>(w, "CpuMap")).OfType<ToggleButton>())
                {
                    bool selected = (fixtureMask & (1UL << int.Parse((string)cpu.Content))) != 0;
                    if (cpu.IsChecked != selected) typeof(ButtonBase).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(cpu, null);
                }
                if (Control<Button>(w, "SaveMask").IsEnabled) Press(w, "SaveMask");
                bool expectedFallback;
                ulong applicationMask = SelfAffinity.Choose(controller.Topology, fixtureMask, out expectedFallback);
                Press(w, "LaunchGame"); Check(Until(() => GameLauncher.Running(controller.Config.Games.Last()), 3000) && !controller.Config.Paused, "UI launch starts fixture and resumes observer");
                Check(Until(() => controller.Engine.AppAffinity.ActiveGames == 1 && controller.Engine.AppAffinity.GamesMask == fixtureMask &&
                    controller.Engine.AppAffinity.Mask == applicationMask && controller.Engine.AppAffinity.Fallback == expectedFallback, 4000),
                    "UI launch exposes the correct application cores for the selected game mask");
                w.Width = 1440; w.Height = 960; Control<ScrollViewer>(w, "LibraryView").ScrollToTop(); Pump(); Capture(w, "application-cores-free");
                Press(w, "PresetAll"); if (Control<Button>(w, "SaveMask").IsEnabled) Press(w, "SaveMask");
                Check(Until(() => controller.Engine.AppAffinity.Fallback, 4000), "UI all-CPU profile exposes last-core fallback"); Control<ScrollViewer>(w, "LibraryView").ScrollToTop(); Pump(); Capture(w, "application-cores-last-fallback");
                Press(w, "PresetEdges"); if (Control<Button>(w, "SaveMask").IsEnabled) Press(w, "SaveMask");
                Press(w, "LaunchGame"); Process[] games = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(fixture)); try { Check(games.Length == 1, "UI launch does not duplicate running game"); } finally { foreach (Process p in games) p.Dispose(); }
                interaction.Pickers.Enqueue(picker =>
                {
                    Check(!Control<Button>(picker, "SelectProcess").IsEnabled, "process picker requires a selected process");
                    Control<TextBox>(picker, "ProcessSearch").Text = Path.GetFileNameWithoutExtension(fixture); Pump(); Press(picker, "RefreshProcesses");
                    ListBox results = Control<ListBox>(picker, "ProcessResults"); Check(results.Items.Count == 1, "process search filters real running fixture after refresh"); results.SelectedIndex = 0; Press(picker, "SelectProcess");
                });
                interaction.Editors.Enqueue(editor => { Press(editor, "ChooseProcess"); Check(Control<TextBox>(editor, "GamePathInput").Text == fixture, "process picker fills exact executable in editor"); Press(editor, "SaveProfile"); Check(editor.IsVisible && interaction.Messages.Last().Contains("пересекаются"), "duplicate target rejected while preserving editor contents"); Press(editor, "CancelProfile"); });
                Press(w, "AddGame");
                interaction.Pickers.Enqueue(picker => { Control<TextBox>(picker, "ProcessSearch").Text = "NumaDefinitelyMissingProcess"; Pump(); Check(Control<ListBox>(picker, "ProcessResults").Items.Count == 0, "process search handles no matches"); Press(picker, "CancelProcess"); });
                interaction.Editors.Enqueue(editor => { Press(editor, "ChooseProcess"); Check(Control<TextBox>(editor, "GamePathInput").Text == "", "cancel process selection leaves draft unchanged"); interaction.OpenPaths.Enqueue(null); Press(editor, "BrowseGameExe"); interaction.OpenPaths.Enqueue(null); Press(editor, "BrowseLaunchExe"); Press(editor, "CancelProfile"); }); Press(w, "AddGame");
                interaction.Editors.Enqueue(editor => { Control<TextBox>(editor, "GameNameInput").Text = "Изменённое имя"; Control<TextBox>(editor, "ArgumentsInput").Text = "--fixture-argument"; Press(editor, "SaveProfile"); });
                Press(w, "EditGame"); Check(controller.Config.Games.Last().Id == fixtureId && controller.Config.Games.Last().Name == "Изменённое имя", "editor preserves ID while changing name and arguments");
                interaction.Editors.Enqueue(editor => { interaction.Answers.Enqueue(MessageBoxResult.Cancel); Press(editor, "DeleteProfile"); Check(editor.IsVisible, "cancel profile deletion keeps editor open"); Press(editor, "CancelProfile"); }); Press(w, "EditGame");
                interaction.Editors.Enqueue(editor => { interaction.Answers.Enqueue(MessageBoxResult.OK); Press(editor, "DeleteProfile"); }); Press(w, "EditGame"); Check(controller.Config.Games.Count == 3 && controller.Config.Games.All(g => g.Id != fixtureId), "confirmed UI deletion removes only selected profile");
                foreach (Process p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(fixture))) using (p) { if (!p.HasExited) { p.Kill(); p.WaitForExit(); } }
                controller.Log.Write("UI journal fixture"); Press(w, "LogNav"); Press(w, "RefreshLog"); Check(Control<TextBox>(w, "LogText").Text.Contains("UI journal fixture"), "journal navigation and refresh read real log");
                Press(w, "DiagnosticsNav"); Press(w, "RefreshDiagnostics"); Check(controller.Health.Current != null && Control<TextBlock>(w, "DiagnosticMemory").Text.Contains("МиБ"), "diagnostics refresh displays measured resource values");
                ResourceSample realSample = controller.Health.Current;
                FieldInfo currentSample = typeof(SelfDiagnostics).GetField("current", BindingFlags.Instance | BindingFlags.NonPublic);
                try
                {
                    ResourceSample calibration = Healthy(30); calibration.CpuOneCorePercent = 60; calibration.CpuMachinePercent = 3.75;
                    calibration.CpuIntervalSeconds = 5; calibration.CpuAverageSeconds = 30; calibration.CpuAverageOneCorePercent = 0.8; calibration.CpuAverageMachinePercent = 0.05;
                    currentSample.SetValue(controller.Health, calibration);
                    typeof(AppController).GetMethod("UpdateDiagnostics", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(controller, null);
                    string cpuText = Control<TextBlock>(w, "DiagnosticCpu").Text;
                    string detail = Control<TextBlock>(w, "DiagnosticCpu").ToolTip as string;
                    Check(cpuText.Contains(3.75.ToString("0.000") + "% всего CPU") && detail.Contains(60.0.ToString("0.00") + "%") && detail.Contains("5"), "CPU display: machine percent is concise and one-core equivalent is available in tooltip");
                    Check(cpuText.Contains(0.05.ToString("0.000") + "%") && cpuText.Contains("30 с"), "CPU display: rolling average is separate from current interval");
                    double oldWidth = w.Width;
                    foreach (int width in new[] { 960, 1220, 1440 })
                    {
                        w.Width = width; Pump(); Capture(w, "cpu-meter-calibration-" + width);
                        TextBlock label = Control<TextBlock>(w, "DiagnosticCpu");
                        Check(label.ActualHeight >= 2 * label.LineHeight && label.ActualWidth > 200, "CPU display: both measurement rows fit wrapped card at width " + width);
                    }
                    w.Width = oldWidth;
                }
                finally { currentSample.SetValue(controller.Health, realSample); }
                int historyBefore = controller.Health.History.Length;
                for (int i = 0; i < 190; i++) controller.Health.Sample(); Pump(); Check(controller.Health.History.Length == historyBefore && historyBefore <= 180, "rapid manual refresh cannot evict timed health history or multiply resource collection");
                Press(w, "SaveDiagnosticReport"); Check(File.Exists(controller.Health.LastReport) && !controller.Reports.PreviousFailure, "manual diagnostic button creates report without triggering emergency");
                Press(w, "OpenReports"); Check(interaction.Revealed == controller.Reports.DirectoryPath, "open reports button targets correct folder");
                Press(w, "LibraryNav"); Press(w, "PresetAll"); if (Control<Button>(w, "SaveMask").IsEnabled) { interaction.Answers.Enqueue(MessageBoxResult.Yes); Control<ListBox>(w, "GameList").SelectedIndex = 1; Pump(); Check(controller.Config.Games[0].CpuMask == controller.Topology.AllMask, "changing selected game can save pending draft"); }
                controller.Save(new Configuration { Paused = true });
                // Import through the actual button refreshes the view for the empty-library case.
                Configuration none = new Configuration { Paused = true }; string nonePath = Path.Combine(directory, "none.json"); ProfileStore.WriteAtomic(nonePath, ProfileStore.Serializer().Serialize(none));
                Press(w, "SettingsNav"); interaction.OpenPaths.Enqueue(nonePath); interaction.Answers.Enqueue(MessageBoxResult.OK); Press(w, "ImportProfiles"); Press(w, "LibraryNav"); Check(Control<Border>(w, "EmptyState").Visibility == Visibility.Visible && Control<StackPanel>(w, "ProfilePanel").Visibility == Visibility.Collapsed, "empty imported library displays coherent empty state");
                Press(w, "SettingsNav"); interaction.OpenPaths.Enqueue(export); interaction.Answers.Enqueue(MessageBoxResult.OK); Press(w, "ImportProfiles"); Press(w, "LibraryNav");
                // Persist failure must preserve draft and restore the switch's previous visual state.
                using (FileStream locked = new FileStream(controller.Store.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    bool before = controller.Config.Games[0].Enabled; Press(w, "EnabledToggle"); Check(controller.Config.Games[0].Enabled == before && Control<CheckBox>(w, "EnabledToggle").IsChecked == before, "failed switch save restores persisted visual state");
                    interaction.Editors.Enqueue(editor => { Control<TextBox>(editor, "GameNameInput").Text = "Draft after disk failure"; Press(editor, "SaveProfile"); Check(editor.IsVisible && Control<TextBox>(editor, "GameNameInput").Text == "Draft after disk failure", "save failure leaves editor open with draft intact"); Press(editor, "CancelProfile"); }); Press(w, "EditGame");
                }
                Check(!controller.Health.IsFatal, "expected disk write failure does not terminate application");
                LegacyKit legacy = new LegacyKit { Name = "UI legacy fixture", Prefix = "NumaUI-" + Guid.NewGuid().ToString("N"), ProcessName = "fake", ExeName = "OldFixture.exe", ShortcutName = "UI fixture", ShortcutOverride = Path.Combine(directory, "old-startup.lnk") };
                environment.Legacy.Add(legacy); ShellLinks.Write(legacy.ShortcutPath, Path.Combine(directory, legacy.ExeName), "--watch", "test");
                using (EventWaitHandle ready = new EventWaitHandle(true, EventResetMode.ManualReset, legacy.Identity + "-Ready"))
                using (EventWaitHandle stop = new EventWaitHandle(false, EventResetMode.ManualReset, legacy.Identity + "-Stop"))
                {
                    Press(w, "SettingsNav"); Press(w, "LegacyRefresh"); Check(Control<Button>(w, "LegacyMigrate").IsEnabled, "legacy status refresh enables transition for detected kit");
                    interaction.Answers.Enqueue(MessageBoxResult.Cancel); Press(w, "LegacyMigrate"); Check(File.Exists(legacy.ShortcutPath) && !stop.WaitOne(0), "cancel legacy transition leaves original watcher and shortcut untouched");
                    interaction.Answers.Enqueue(MessageBoxResult.OK); Press(w, "LegacyMigrate"); Check(!File.Exists(legacy.ShortcutPath) && stop.WaitOne(0) && Directory.GetFiles(Path.Combine(directory, "legacy-backup")).Length == 1, "UI legacy transition backs up shortcut and sends graceful stop");
                    ready.Reset(); Press(w, "LegacyRefresh"); Check(!Descendants(Control<StackPanel>(w, "SettingsBody")).OfType<Button>().Any(b => b.Name == "LegacyMigrate"), "legacy controls disappear after transition completes");
                }
                Press(w, "LibraryNav");
                VisualMatrix(app, w, controller, interaction);
                w.WindowState = WindowState.Minimized; Pump(); Check(!w.IsVisible, "minimize with clean draft closes interface to tray"); controller.ShowWindow(); w = MainWindow(app);
                Press(w, controller.Config.Games[0].CpuMask == controller.Topology.AllMask ? "PresetPhysical" : "PresetAll"); string minimizedDraft = Control<TextBlock>(w, "MaskLabel").Text;
                Check(Control<Button>(w, "SaveMask").IsEnabled, "minimize-draft fixture has a real unsaved selection");
                w.WindowState = WindowState.Minimized; Pump(); Check(!w.IsVisible, "minimize with pending edits hides window without losing draft"); controller.ShowWindow(); Check(object.ReferenceEquals(w, MainWindow(app)) && Control<TextBlock>(w, "MaskLabel").Text == minimizedDraft, "tray reopen preserves minimized unsaved selection");
                if (Control<Button>(w, "SaveMask").IsEnabled) { interaction.Answers.Enqueue(MessageBoxResult.No); Press(w, "LibraryNav"); }
                w.WindowState = WindowState.Maximized; Pump(); Check(w.WindowState == WindowState.Maximized, "maximize expands main window"); w.WindowState = WindowState.Normal; Pump();
                for (int i = 0; i < 10; i++) { w.Close(); controller.ShowWindow(); w = MainWindow(app); Press(w, "SettingsNav"); Press(w, "DiagnosticsNav"); Press(w, "LibraryNav"); }
                Check(!controller.Health.IsFatal && controller.Config.Games.Count == 3, "ten full window and navigation cycles preserve library and health");
                Check(interaction.Answers.Count == 0 && interaction.OpenPaths.Count == 0 && interaction.SavePaths.Count == 0 && interaction.Editors.Count == 0, "all expected UI branches and dialog responses were consumed");
                w.Close();
            }
            RecoveryFlow(app);
            LargeLibraryAndCpuMap(app);
            ProfileLimitUiCheck(app);
            Release15UiChecks(app);
            LocalizationChecks(app);
            TrayFlow(app);
        }
        private static void Capture(Window window, string name)
        {
            window.UpdateLayout(); FrameworkElement surface = (FrameworkElement)window.Content;
            double width = surface.ActualWidth + surface.Margin.Left + surface.Margin.Right, height = surface.ActualHeight + surface.Margin.Top + surface.Margin.Bottom;
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
            var background = new DrawingVisual(); using (DrawingContext drawing = background.RenderOpen()) drawing.DrawRectangle((Brush)Application.Current.FindResource("Bg"), null, new Rect(0, 0, width, height));
            bitmap.Render(background); bitmap.Render(surface); var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            string directory = Path.Combine(root, "visuals"); Directory.CreateDirectory(directory); using (FileStream file = File.Create(Path.Combine(directory, name + ".png"))) png.Save(file);
        }
        private static void VisualMatrix(Application app, Window w, AppController controller, ScriptedInteraction interaction, string prefix = "")
        {
            foreach (Size size in new[] { new Size(960, 650), new Size(1220, 870), new Size(1440, 960) })
            {
                w.Width = size.Width; w.Height = size.Height; Pump(); Press(w, "LibraryNav");
                Control<ScrollViewer>(w, "LibraryView").ScrollToTop(); Pump(); Capture(w, prefix + "library-" + (int)size.Width);
                TextBlock cpu = Control<TextBlock>(w, "ProcessorName"), mask = Control<TextBlock>(w, "MaskLabel");
                Point a = cpu.TranslatePoint(new Point(0, 0), w), b = mask.TranslatePoint(new Point(0, 0), w);
                Check(a.Y >= b.Y + mask.ActualHeight - 1 || a.X + cpu.ActualWidth <= b.X + 1, "processor name and mask never overlap at width " + size.Width);
                Control<ScrollViewer>(w, "LibraryView").ScrollToEnd(); Pump(); Check(Control<Button>(w, "SaveMask").TransformToAncestor(w).TransformBounds(new Rect(0, 0, Control<Button>(w, "SaveMask").ActualWidth, Control<Button>(w, "SaveMask").ActualHeight)).Bottom < w.ActualHeight, "save action remains reachable by scrolling at width " + size.Width);
                ScrollViewer viewer = Control<ScrollViewer>(w, "LibraryView");
                if (viewer.ScrollableHeight > 0)
                {
                    viewer.ScrollToTop(); Pump(); ScrollBar bar = Descendants(viewer).OfType<ScrollBar>().First(s => s.Orientation == Orientation.Vertical && s.Maximum > 0);
                    Track track = (Track)bar.Template.FindName("PART_Track", bar); Check(track.Orientation == Orientation.Vertical, "custom scroll track follows vertical orientation at " + size.Width);
                    ScrollBar.PageDownCommand.Execute(null, bar); Pump(); Check(viewer.VerticalOffset > 0, "custom scrollbar page-down command scrolls content at " + size.Width);
                    viewer.ScrollToTop(); Pump(); track.Thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent }); track.Thumb.RaiseEvent(new DragDeltaEventArgs(0, 20) { RoutedEvent = Thumb.DragDeltaEvent }); track.Thumb.RaiseEvent(new DragCompletedEventArgs(0, 20, false) { RoutedEvent = Thumb.DragCompletedEvent }); Pump();
                    Check(viewer.VerticalOffset > 0, "custom scrollbar thumb drag scrolls content at " + size.Width);
                }
                foreach (string nav in new[] { "SettingsNav", "DiagnosticsNav", "LogNav" })
                {
                    Press(w, nav);
                    ScrollViewer page = nav == "LogNav" ? null : Control<ScrollViewer>(w, nav == "SettingsNav" ? "SettingsView" : "DiagnosticsView");
                    if (page != null) { page.ScrollToTop(); Pump(); }
                    Capture(w, prefix + nav + "-" + (int)size.Width);
                    if (page != null) { page.ScrollToEnd(); Pump(); Capture(w, prefix + nav + "-bottom-" + (int)size.Width); }
                }
            }
            w.Width = 1220; w.Height = 870; Press(w, "LibraryNav");
            interaction.Editors.Enqueue(editor => {
                Capture(editor, prefix + "profile-editor");
                CheckBox priority = Control<CheckBox>(editor, "HighPriorityToggle"); priority.BringIntoView(); Pump();
                Capture(editor, prefix + "profile-editor-bottom");
                foreach (string label in new[] { "Название в библиотеке", "Имя процесса без .exe", "Точный путь к EXE игры · можно оставить пустым", "Цель запуска · EXE или ссылка лаунчера", "Аргументы запуска · только для EXE" })
                {
                    TextBlock caption = Descendants(editor).OfType<TextBlock>().Single(text => text.Text == L10n.T(label));
                    Check(Contrast(((SolidColorBrush)caption.Foreground).Color.ToString(), ((SolidColorBrush)editor.Background).Color.ToString()) >= 4.5, "profile field label has readable contrast / " + prefix + " / " + L10n.T(label));
                }
                Check(priority.TransformToAncestor(editor).TransformBounds(new Rect(0, 0, priority.ActualWidth, priority.ActualHeight)).Bottom < editor.ActualHeight, "profile priority option remains reachable by scrolling / " + prefix);
                Press(editor, "CancelProfile");
            }); Press(w, "EditGame");
            interaction.Pickers.Enqueue(picker => { Capture(picker, prefix + "process-picker"); Press(picker, "CancelProcess"); });
            interaction.Editors.Enqueue(editor => { Press(editor, "ChooseProcess"); Press(editor, "CancelProfile"); }); Press(w, "AddGame");
            AppDialog dialog = new AppDialog(w, L10n.T("Выбор ЦП доступен в карточке игры после сохранения. Выключение наблюдателя или удаление профиля не меняет уже назначенную маску процесса."), L10n.T("Проверьте параметры"), MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            dialog.Loaded += delegate { dialog.Dispatcher.BeginInvoke(new Action(delegate { Capture(dialog, prefix + "themed-dialog"); Control<Button>(dialog, "AnswerCancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); })); };
            dialog.ShowDialog(); Check(dialog.Answer == MessageBoxResult.Cancel, "themed confirmation has working cancel action");
            foreach (MessageBoxResult answer in new[] { MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel })
            {
                AppDialog choice = new AppDialog(w, L10n.T("Сохранить изменения привязки для «") + "Example game»?", L10n.T("Несохранённые изменения"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                choice.Loaded += delegate { choice.Dispatcher.BeginInvoke(new Action(delegate { if (answer == MessageBoxResult.Yes) Capture(choice, prefix + "three-way-dialog"); Press(choice, "Answer" + answer); })); }; choice.ShowDialog();
                Check(choice.Answer == answer, "themed three-way dialog returns " + answer);
            }
        }
        private static void RecoveryFlow(Application app)
        {
            string directory = Path.Combine(root, "recovery-ui"); Configuration c = new Configuration(); new ProfileStore(directory).Save(c);
            ScriptedInteraction dialogs = new ScriptedInteraction(); AppEnvironment env = new AppEnvironment(directory) { Interaction = dialogs, ExitOnFatal = false };
            using (AppController failed = new AppController(app, false, directory, env))
            {
                failed.Start(); failed.Critical("injected-recovery-test", new NullReferenceException("fixture"));
                Check(failed.Health.IsFatal && failed.Reports.PreviousFailure && File.Exists(failed.Health.LastReport), "critical exception creates durable restart marker and bug report");
            }
            using (AppController recovered = new AppController(app, false, directory, env))
            {
                Check(recovered.Config.Paused, "next startup pauses observers after prior fatal failure"); recovered.Start(); Window w = MainWindow(app); Press(w, "PauseAll");
                Check(!recovered.Config.Paused && !recovered.Reports.PreviousFailure, "explicit continue clears fault marker after successful save"); w.Close();
            }
            string bad = Path.Combine(root, "blocked-report-directory"); File.WriteAllText(bad, "file instead of directory"); int stops = 0;
            using (SelfDiagnostics monitor = new SelfDiagnostics(app.Dispatcher, null, new BugReports(bad, new AppLog(root)), new AppLog(root), () => new { Fixture = true }, reason => stops++))
            {
                monitor.Fatal("fallback-report-test", new Exception("fixture")); monitor.Fatal("duplicate", null);
                Check(stops == 1 && File.Exists(monitor.LastReport) && monitor.LastReport.StartsWith(AppDomain.CurrentDomain.BaseDirectory, StringComparison.OrdinalIgnoreCase) && monitor.LastReport.Contains("recovery"), "unwritable report folder uses portable recovery and fatal handling is idempotent");
            }
        }
        private static void TrayFlow(Application app)
        {
            string directory = Path.Combine(root, "tray-ui"); Configuration c = SampleConfiguration(Topology.Detect()); c.Paused = true; new ProfileStore(directory).Save(c);
            ScriptedInteraction dialogs = new ScriptedInteraction(); AppEnvironment env = new AppEnvironment(directory) { Interaction = dialogs, ExitOnFatal = false };
            using (AppController controller = new AppController(app, false, directory, env))
            {
                controller.Start();
                var tray = (System.Windows.Forms.NotifyIcon)typeof(AppController).GetField("tray", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);
                Action<string> click = text => tray.ContextMenuStrip.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().Single(i => i.Text == text).PerformClick();
                click("Открыть NUMA Control"); Window w = MainWindow(app); Check(w.IsVisible, "tray open command restores interface");
                click("Пауза наблюдения"); Check(!controller.Config.Paused, "tray pause command resumes observation"); click("Пауза наблюдения"); Check(controller.Config.Paused, "tray pause command pauses observation");
                click("Автозапуск Windows"); Check(File.Exists(env.StartupPath), "tray autostart enables isolated shortcut"); click("Автозапуск Windows"); Check(!File.Exists(env.StartupPath), "tray autostart disables isolated shortcut");
                click("Самодиагностика"); Check(Control<ScrollViewer>(w, "DiagnosticsView").Visibility == Visibility.Visible, "tray diagnostics opens resource screen");
                Press(w, "LibraryNav");
                string saved = Control<TextBlock>(w, "MaskLabel").Text;
                ToggleButton tile = Descendants(Control<WrapPanel>(w, "CpuMap")).OfType<ToggleButton>().First();
                typeof(ButtonBase).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(tile, null);
                string unsaved = Control<TextBlock>(w, "MaskLabel").Text;
                dialogs.Answers.Enqueue(MessageBoxResult.Cancel); click("Выход");
                Check(w.IsVisible && !app.Dispatcher.HasShutdownStarted && Control<TextBlock>(w, "MaskLabel").Text == unsaved &&
                    unsaved != saved, "tray exit cancellation preserves unsaved draft and running app");
                dialogs.Answers.Enqueue(MessageBoxResult.No); click("Выход"); if (!app.Dispatcher.HasShutdownStarted) Pump(); Check(!w.IsVisible, "tray exit confirmation closes interface and requests shutdown");
            }
        }
        private static void LargeLibraryAndCpuMap(Application app)
        {
            string directory = Path.Combine(root, "large-preview");
            using (AppController preview = new AppController(app, true, directory, new AppEnvironment(directory)))
            {
                preview.Start(); preview.Config.Games.Clear();
                for (int i = 0; i < 100; i++) preview.Config.Games.Add(new GameProfile { Name = "Игра " + i + " " + new string('Ж', 65), ProcessName = "NumaMissingLarge" + i, Mask = "1" });
                preview.ShowWindow(); Window w = app.Windows.Cast<Window>().Single(x => x.IsVisible && x.Title.EndsWith("— предпросмотр")); w.Width = 960; w.Height = 650;
                ListBox list = Control<ListBox>(w, "GameList"); list.SelectedIndex = 99; list.ScrollIntoView(list.SelectedItem); Pump();
                Check(Control<TextBlock>(w, "SelectedName").Text.StartsWith("Игра 99"), "100-profile library selects last game without losing identity");
                TextBlock title = Control<TextBlock>(w, "SelectedName"); Check(title.ActualWidth > 0 && title.TextTrimming == TextTrimming.CharacterEllipsis, "long game names use bounded title and tooltip"); Capture(w, "library-100-profiles"); w.Close();
                preview.Topology.Count = 64; preview.Topology.AllMask = ulong.MaxValue; preview.Topology.Cores.Clear();
                for (int i = 0; i < 32; i++) preview.Topology.Cores.Add(new CpuCore { Index = i, Mask = 3UL << (i * 2) });
                preview.Config = SampleConfiguration(preview.Topology); preview.ShowWindow(); w = app.Windows.Cast<Window>().Single(x => x.IsVisible && x.Title.EndsWith("— предпросмотр")); Press(w, "PresetAll"); Press(w, "SaveMask");
                Check(Descendants(Control<WrapPanel>(w, "CpuMap")).OfType<ToggleButton>().Count() == 64 && preview.Config.Games[0].CpuMask == ulong.MaxValue, "64-CPU UI renders every toggle and saves CPU 63 without sign overflow");
                Control<ScrollViewer>(w, "LibraryView").ScrollToEnd(); Pump(); Capture(w, "cpu-map-64"); w.Close();
            }
        }
    }
}
