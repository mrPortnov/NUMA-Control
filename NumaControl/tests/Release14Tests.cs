using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace NumaControl
{
    internal static partial class Tests
    {
        private static void Release14UiChecks(AppController controller, Window window)
        {
            TextBlock[] metrics = new[] { "ObserverMetric", "CpuMetric", "AppCoreMetric" }.Select(n => Control<TextBlock>(window, n)).ToArray();
            Check(metrics.Select(m => m.FontSize).Distinct().Count() == 1 && metrics[0].FontSize <= 18, "library summary cards use one restrained metric size");
            Check(Control<TextBlock>(window, "SelectedName").FontSize <= 20, "selected game heading has a balanced size");
            Check(Control<Image>(window, "SelectedGameIcon").Source == controller.Icons.Get(controller.Config.Games[0]), "selected game heading displays the cached game icon");
            TimeSpan normal = controller.Platform.ToastDuration; controller.Platform.ToastDuration = TimeSpan.FromMilliseconds(350);
            try
            {
                Press(window, "DesktopShortcut");
                Check(Control<TextBlock>(window, "Toast").Text.Contains("создан"), "desktop shortcut feedback appears immediately");
                WaitUi(200); Press(window, "DiagnosticsNav"); Press(window, "SaveDiagnosticReport");
                Check(Control<TextBlock>(window, "Toast").Text.Contains("Отчёт сохранён"), "new action replaces earlier footer message");
                WaitUi(200);
                Check(Control<TextBlock>(window, "Toast").Text.Contains("Отчёт сохранён"), "old footer deadline cannot clear the newer message");
                Check(Until(delegate { Pump(); return Control<TextBlock>(window, "Toast").Text == AppController.IdleFooter; }, 2000), "footer returns to neutral text after the most recent action timer");
                Check(Control<TextBlock>(window, "Toast").ToolTip == null, "expired footer message also clears its tooltip");
            }
            finally { controller.Platform.ToastDuration = normal; }
            Check(Control<TextBlock>(window, "DiagnosticMemory").Text.Contains(".NET:"), "dense memory card includes managed memory without another OS query");
            Check(Control<TextBlock>(window, "DiagnosticHandles").Text.Contains("Профили:"), "dense process card includes enabled profile count");
            Check(Control<TextBlock>(window, "DiagnosticHeartbeat").Text.Contains("обход:") && Control<TextBlock>(window, "DiagnosticHeartbeat").Text.Contains("циклов:"), "diagnostics exposes observer cycle duration and count");
            Capture(window, "release-1.4-diagnostics"); Press(window, "LibraryNav"); Control<ScrollViewer>(window, "LibraryView").ScrollToTop(); Pump(); Capture(window, "release-1.4-library");
        }
        private static void WaitUi(int milliseconds)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < milliseconds) { Pump(); Thread.Sleep(10); }
        }
        private static void ProfileLimitUiCheck(Application app)
        {
            string directory = Path.Combine(root, "profile-limit"); ScriptedInteraction interaction = new ScriptedInteraction();
            using (var controller = new AppController(app, true, directory, new AppEnvironment(directory) { Interaction = interaction, ExitOnFatal = false }))
            {
                controller.Config.Games.Clear();
                for (int i = 0; i < Configuration.MaxGames; i++) controller.Config.Games.Add(new GameProfile { Name = "Профиль " + i, ProcessName = "NumaLimitMissing" + i, Mask = "1", Enabled = true });
                controller.Config.Validate(controller.Topology, true); controller.Start(); controller.ShowWindow(); Window window = app.Windows.Cast<Window>().Single(w => w.IsVisible && w.Title.EndsWith("— предпросмотр"));
                string[] ids = controller.Config.Games.Select(g => g.Id).ToArray();
                interaction.Editors.Enqueue(editor =>
                {
                    Control<TextBox>(editor, "GameNameInput").Text = "Лишний профиль"; Control<TextBox>(editor, "ProcessNameInput").Text = "NumaOverLimit";
                    Press(editor, "SaveProfile");
                    Check(editor.IsVisible && interaction.Messages.Last().Contains("предел 100"), "101st profile is rejected with a useful message while editor stays open");
                    Press(editor, "CancelProfile");
                });
                Press(window, "AddGame");
                Check(controller.Config.Games.Count == Configuration.MaxGames && controller.Config.Games.Select(g => g.Id).SequenceEqual(ids) && !controller.Health.IsFatal, "profile limit preserves every existing profile and keeps application healthy");
                window.Close();
            }
            string links = Path.Combine(root, "uninstall-links"); Directory.CreateDirectory(links);
            string own = Path.Combine(links, "installed", "NUMA-Control.exe"), other = Path.Combine(links, "portable", "NUMA-Control.exe");
            string startup = Path.Combine(links, "startup.lnk"), ours = Path.Combine(links, "Game — NUMA.lnk"), foreign = Path.Combine(links, "Other — NUMA.lnk");
            ShellLinks.Write(startup, own, "--tray", "test"); ShellLinks.Write(ours, own, "--launch fixture", "test"); ShellLinks.Write(foreign, other, "--launch fixture", "test");
            InstallMaintenance.Cleanup(own, startup, links);
            Check(!File.Exists(startup) && !File.Exists(ours) && File.Exists(foreign), "uninstaller cleans only shortcuts targeting its own installed executable");
        }
    }
}
