using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;

namespace NumaControl
{
    internal static partial class Tests
    {
        private static void Release13Checks()
        {
            Check(Program.ShouldShow(new string[0]), "launch: ordinary double-click opens the main window");
            Check(!Program.ShouldShow(new[] { "--tray" }) && !Program.ShouldShow(new[] { "--launch", "fixture" }), "launch: autostart and game shortcuts can stay in tray");
            Check(Program.ShouldShow(new[] { "--show", "--tray" }) && Program.ShouldShow(new[] { "--preview" }), "launch: explicit show and preview open a window");
            foreach (int threads in new[] { 1, 2, 4 })
            foreach (int coreCount in new[] { 1, 2, 3, 6, 8, 16 })
            {
                if (threads * coreCount > 64) continue;
                Topology cpu = new Topology { Count = threads * coreCount, Groups = 1, AllMask = Topology.FullMask(threads * coreCount), Name = "Synthetic CPU" };
                for (int i = 0; i < coreCount; i++) cpu.Cores.Add(new CpuCore { Index = i, Mask = Topology.FullMask(threads) << (i * threads), Efficiency = i % 2 });
                Check(Topology.Bits(cpu.OnePerCore()) == coreCount, "universal presets: one logical thread per actual core / " + coreCount + " x " + threads);
                ulong expected = coreCount > 2 ? cpu.AllMask & ~cpu.Cores.First().Mask & ~cpu.Cores.Last().Mask : cpu.AllMask;
                Check(cpu.RecommendedMask() == expected && cpu.WithoutEdgeCores() == expected, "universal presets: edge exclusion follows whole physical cores / " + coreCount + " x " + threads);
            }
            Topology host = Topology.Detect(); string cache = Path.Combine(root, "cpu-info");
            Check(!CpuInfoStore.Save(cache, host) && File.Exists(Path.Combine(cache, "processor.json")), "processor cache: first detection saves portable hardware data");
            DateTime write = File.GetLastWriteTimeUtc(Path.Combine(cache, "processor.json"));
            Check(!CpuInfoStore.Save(cache, host) && File.GetLastWriteTimeUtc(Path.Combine(cache, "processor.json")) == write, "processor cache: unchanged hardware does not rewrite the snapshot");
            string originalName = host.Name; host.Name = "Another processor family";
            Check(CpuInfoStore.Save(cache, host), "processor cache: processor replacement is detected even when thread count matches");
            host.Name = originalName;
            File.WriteAllText(Path.Combine(cache, "processor.json"), "broken json");
            Check(!CpuInfoStore.Save(cache, host), "processor cache: corrupt cached data is rebuilt from live topology");
            string migration = Path.Combine(root, "portable-migration"), previous = Path.Combine(root, "old-appdata");
            ProfileStore oldStore = new ProfileStore(previous); oldStore.Save(SampleConfiguration(host));
            string target = LocalData.Prepare(migration, previous);
            Check(target == Path.Combine(migration, "data") && File.Exists(Path.Combine(target, "profiles.json")) && File.Exists(oldStore.FilePath), "portable data: old profiles are copied locally without destroying the source");
            File.WriteAllText(Path.Combine(target, "profiles.json"), "preserve current data"); LocalData.Prepare(migration, previous);
            Check(File.ReadAllText(Path.Combine(target, "profiles.json")) == "preserve current data", "portable data: existing local profiles are never replaced by migration");
            string blocked = Path.Combine(root, "portable-blocked"); Directory.CreateDirectory(blocked); File.WriteAllText(Path.Combine(blocked, "data"), "file instead of directory");
            Check(LocalData.Prepare(blocked, previous) == previous, "portable data: unwritable application folder uses the explicit AppData fallback");
            string iconRoot = Path.Combine(root, "icon-cache");
            string release = Path.GetFullPath(Path.Combine(root, "..", "..", "..", "NUMA-Control.exe"));
            GameProfile profile = new GameProfile { ExePath = release, LaunchTarget = release };
            GameIcons icons = new GameIcons(iconRoot); var icon = icons.Get(profile);
            Check(icon != null && icon.IsFrozen && Directory.GetFiles(Path.Combine(iconRoot, "icons"), "*.png").Length == 1, "game icons: EXE resource is extracted and saved as a local PNG");
            Check(object.ReferenceEquals(icon, icons.Get(profile)), "game icons: repeated UI rebuild reuses a frozen image");
            Check(new GameIcons(iconRoot).Get(profile) != null, "game icons: saved PNG is usable after restart");
            File.WriteAllText(Directory.GetFiles(Path.Combine(iconRoot, "icons"), "*.png").Single(), "broken image");
            Check(new GameIcons(iconRoot).Get(profile) != null, "game icons: corrupt cache is repaired from the executable");
            Check(icons.Get(new GameProfile { ExePath = Path.Combine(root, "missing-icon.exe") }) == null, "game icons: missing image safely falls back to initials");
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("NumaControl.app.ico"))
            {
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                Check(decoder.Frames.Count == 9 && decoder.Frames.Max(f => f.PixelWidth) == 256 && decoder.Frames.Min(f => f.PixelWidth) == 16, "application icon: nine native sizes from 16 to 256 pixels");
            }
        }
        private static void Release13UiChecks(AppController controller, Window window)
        {
            string replacement = Path.Combine(root, "hardware-replacement"); Topology oldCpu = Topology.Detect(); oldCpu.Name = "Earlier CPU"; CpuInfoStore.Save(replacement, oldCpu);
            Configuration oldConfig = SampleConfiguration(controller.Topology); oldConfig.Games[0].Mask = "1"; new ProfileStore(replacement).Save(oldConfig);
            using (var replaced = new AppController(Application.Current, false, replacement))
                Check(replaced.HardwareChanged && replaced.Config.Paused && replaced.Config.Games[0].Mask == "1", "hardware replacement: live redetection pauses observers and preserves user's chosen mask");
            Check(Control<TextBlock>(window, "CpuMetric").Text.Contains("ядер") && Control<TextBlock>(window, "CpuMetric").Text.Contains("потоков"), "CPU card explains physical cores and logical threads");
            foreach (string name in new[] { "LibraryNav", "SettingsNav", "LogNav", "DiagnosticsNav" })
            {
                Image image = Descendants(Control<Button>(window, name)).OfType<Image>().Single();
                Check(image.Width == 16 && image.Height == 16, "sidebar vector icon has the same size / " + name);
            }
            Press(window, "PresetAll"); if (Control<Button>(window, "SaveMask").IsEnabled) Press(window, "SaveMask");
            ToggleButton[] toggles = Descendants(Control<WrapPanel>(window, "CpuMap")).OfType<ToggleButton>().ToArray();
            ulong custom = 0;
            for (int i = 0; i < toggles.Length; i++)
            {
                bool want = i % 3 == 0;
                if (toggles[i].IsChecked != want) typeof(ButtonBase).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(toggles[i], null);
                if (want) custom |= 1UL << i;
            }
            if (Control<Button>(window, "SaveMask").IsEnabled) Press(window, "SaveMask");
            Check(controller.Config.Games[0].CpuMask == custom, "arbitrary custom CPU selection by clicks is saved exactly");
            Press(window, "SettingsNav");
            Check(!Descendants(Control<StackPanel>(window, "SettingsBody")).OfType<Button>().Any(b => b.Name == "LegacyMigrate"), "settings hide obsolete migration when no earlier watcher exists");
            Press(window, "DiagnosticsNav"); Press(window, "RefreshDiagnostics");
            Check(Control<TextBlock>(window, "DiagnosticMemory").Text.Contains("/ 512 МиБ") && Control<TextBlock>(window, "DiagnosticMemory").Text.Contains("/ 768 МиБ"), "diagnostic memory includes units for both limits");
            Check(!Control<TextBlock>(window, "DiagnosticMemory").Text.Contains("МиБ /"), "memory card places the common MiB unit after current and limit");
            Check(Control<TextBlock>(window, "DiagnosticHandles").Text.Contains("/ 6000 дескрипторов"), "resource card places descriptor unit after current and limit");
            Capture(window, "release-1.3-diagnostics");
            var tray = (System.Windows.Forms.NotifyIcon)typeof(AppController).GetField("tray", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);
            var menu = tray.ContextMenuStrip; menu.PerformLayout(); menu.Size = menu.GetPreferredSize(System.Drawing.Size.Empty);
            Check(menu.Renderer is TrayStyle && menu.BackColor.R < 50 && menu.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().All(i => i.Image != null), "tray menu uses the app palette and consistent command icons");
            using (var bitmap = new System.Drawing.Bitmap(menu.Width, menu.Height))
            { menu.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height)); bitmap.Save(Path.Combine(root, "visuals", "tray-menu.png"), System.Drawing.Imaging.ImageFormat.Png); }
            Press(window, "LibraryNav");
            LiveIconCheck(controller, window);
        }
        private static byte[] IconPixel(System.Windows.Media.ImageSource image)
        {
            var converted = new FormatConvertedBitmap((BitmapSource)image, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            byte[] pixel = new byte[4]; converted.CopyPixels(new Int32Rect(15, 32, 1, 1), pixel, 4, 0); return pixel;
        }
        private static void LiveIconCheck(AppController controller, Window window)
        {
            string fixture = Directory.GetFiles(root, "NumaIconFixture*.exe").Single();
            ScriptedInteraction interaction = (ScriptedInteraction)controller.Platform.Interaction;
            using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fixture) { UseShellExecute = false, CreateNoWindow = true }))
            try
            {
                interaction.Pickers.Enqueue(picker =>
                {
                    Control<TextBox>(picker, "ProcessSearch").Text = System.IO.Path.GetFileNameWithoutExtension(fixture); Pump(); Press(picker, "RefreshProcesses");
                    ListBox results = Control<ListBox>(picker, "ProcessResults");
                    Check(results.Items.Count == 1 && ((ProcessEntry)results.Items[0]).Pid == process.Id, "live icon: picker finds the running custom-icon process");
                    results.SelectedIndex = 0; Press(picker, "SelectProcess");
                });
                interaction.Editors.Enqueue(editor =>
                {
                    Press(editor, "ChooseProcess");
                    Check(Control<TextBox>(editor, "GamePathInput").Text == fixture, "live icon: chosen process supplies the exact EXE for icon extraction");
                    Control<TextBox>(editor, "GameNameInput").Text = "Игра со своим значком"; Press(editor, "SaveProfile");
                });
                string iconDirectory = Path.Combine(controller.Store.DirectoryPath, "icons"); string[] before = Directory.Exists(iconDirectory) ? Directory.GetFiles(iconDirectory, "*.png") : new string[0];
                Press(window, "AddGame");
                GameRow row = Control<ListBox>(window, "GameList").Items.Cast<GameRow>().Single(r => r.Game.ExePath == fixture);
                byte[] actual = IconPixel(row.Icon);
                Check(actual[1] > 180 && actual[2] < 150 && actual[3] > 240, "live icon: extracted image contains fixture's green emblem, not the app logo or initials");
                string cached = Directory.GetFiles(iconDirectory, "*.png").Except(before).Single();
                Check(File.Exists(cached), "live icon: selected process icon is saved beside profile data");
                process.Kill(); process.WaitForExit(); controller.Engine.Refresh();
                using (FileStream locked = new FileStream(fixture, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var restored = new GameIcons(controller.Store.DirectoryPath).Get(row.Game);
                    Check(restored != null && IconPixel(restored).SequenceEqual(actual), "live icon: next cache instance restores the exact icon after process exit with EXE inaccessible");
                }
                Capture(window, "live-process-icon");
                interaction.Answers.Enqueue(MessageBoxResult.OK);
                interaction.Editors.Enqueue(editor => Press(editor, "DeleteProfile")); Press(window, "EditGame");
                Check(!controller.Config.Games.Any(g => g.ExePath == fixture), "live icon: test profile cleanup preserves other game profiles");
            }
            finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(); } }
        }
    }
}
