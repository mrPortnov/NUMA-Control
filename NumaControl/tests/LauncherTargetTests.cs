using System;
using System.Diagnostics;
using System.IO;

namespace NumaControl
{
    internal static partial class Tests
    {
        private static void LauncherTargetChecks(Topology topology, string fixture)
        {
            string folder = Path.Combine(root, "launcher-targets");
            Directory.CreateDirectory(folder);
            const string epic = "com.epicgames.launcher://apps/fn%3Aexample%3AFortnite?action=launch&silent=true";
            foreach (string uri in new[] { epic, "com.epicgames.launcher://apps/Example?action=launch", "battlenet://wow", "uplay://launch/123/0" })
            {
                var profile = new GameProfile { ProcessName = "NumaMissingGameFixture", LaunchTarget = uri, Arguments = "--ignored" };
                ProcessStartInfo sent = null;
                GameLauncher.Launch(profile, topology.AllMask, info => { sent = info; return null; });
                Check(sent != null && sent.UseShellExecute && sent.FileName == uri && string.IsNullOrEmpty(sent.Arguments),
                    "launcher link is sent intact to Windows without EXE arguments: " + uri);
            }
            foreach (string uri in new[] { epic + "&extra=1", epic.Replace("%3A", "%22"), epic.Replace("action=launch", "action=install"),
                "battlenet://wow?redirect=example", "battlenet://wow/extra", "uplay://launch/123/0#extra", "https://example.invalid/game" })
                Reject(() => Configuration.ValidateLaunch(uri), "unsupported launcher link is rejected");

            string shortcut = Path.Combine(folder, "Game.url");
            File.WriteAllText(shortcut, "[InternetShortcut]\r\nURL=" + epic + "\r\n");
            Check(LauncherTargets.ReadShortcut(shortcut).Target == epic, "game URL imports its exact Epic launch target");
            File.AppendAllText(shortcut, "URL=uplay://launch/123/0\r\n");
            Reject(() => LauncherTargets.ReadShortcut(shortcut), "ambiguous shortcut URL is rejected");
            File.WriteAllText(shortcut, "[InternetShortcut]\r\nURL=https://example.invalid/game\r\n");
            Reject(() => LauncherTargets.ReadShortcut(shortcut), "ordinary website shortcut is rejected");

            string link = Path.Combine(folder, "Launcher.lnk");
            ShellLinks.Write(link, fixture, "--productcode=example", "Test game");
            LauncherTarget imported = LauncherTargets.ReadShortcut(link);
            Check(imported.Target == fixture && imported.Arguments == "--productcode=example" && imported.Directory == Path.GetDirectoryName(fixture),
                "Windows launcher shortcut imports target, arguments and working directory");
            var configuration = new Configuration();
            configuration.Games.Add(new GameProfile { ProcessName = "NumaMissingGameFixture", LaunchTarget = imported.Target,
                Arguments = imported.Arguments, LaunchDirectory = imported.Directory, LaunchViaShell = true });
            var store = new ProfileStore(folder);
            store.Save(configuration);
            GameProfile restored = store.Load(topology).Games[0];
            ProcessStartInfo launch = null;
            GameLauncher.Launch(restored, topology.AllMask, info => { launch = info; return null; });
            Check(launch != null && launch.UseShellExecute && launch.FileName == fixture && launch.Arguments == imported.Arguments && launch.WorkingDirectory == imported.Directory,
                "imported launcher shortcut keeps its launch settings after restart");

            string binary = Path.Combine(folder, "FortniteClient-Win64-Shipping.exe");
            File.Copy(fixture, binary);
            var fortnite = new GameProfile { ProcessName = Path.GetFileNameWithoutExtension(binary), ExePath = binary };
            Func<ProcessStartInfo, Process> capture = info => { launch = info; return null; };
            foreach (string target in new[] { "", binary })
            {
                fortnite.LaunchTarget = target;
                launch = null;
                GameLauncher.Launch(fortnite, topology.AllMask, capture);
                Check(launch != null && !launch.UseShellExecute && launch.FileName == binary && fortnite.LaunchTarget == target,
                    "Fortnite uses the same EXE fallback as other profiles");
            }
            File.WriteAllText(shortcut, "[InternetShortcut]\r\nURL=" + epic + "\r\n");
            fortnite.LaunchTarget = LauncherTargets.ReadShortcut(shortcut).Target;
            launch = null;
            GameLauncher.Launch(fortnite, topology.AllMask, capture);
            Check(launch != null && launch.UseShellExecute && launch.FileName == epic && fortnite.ExePath == binary,
                "Fortnite uses an imported Epic shortcut without changing the monitored game EXE");
            FortniteCpuSetChecks(topology, binary);
        }

        private static void FortniteCpuSetChecks(Topology topology, string executable)
        {
            ulong chosen = 1UL << Topology.FirstBit(topology.AllMask);
            var profile = new GameProfile { Name = "CPU Sets fixture", ProcessName = Path.GetFileNameWithoutExtension(executable),
                ExePath = executable, Mask = chosen.ToString("X"), Enabled = true, HighPriority = false };
            var config = new Configuration();
            config.Games.Add(profile);
            using (Process game = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false }))
            using (var engine = new WatchEngine(config, topology, new AppLog(Path.Combine(root, "cpu-sets-log"))))
            try
            {
                using (var handle = Native.Open(game.Id, Native.Query | CpuSets.SetLimited))
                {
                    ulong hardMask = Native.ReadMask(handle);
                    uint priority = Native.ReadPriority(handle);
                    GameStatus status = engine.Scan(config)[profile.Id];
                    Check(status.Running == 1 && status.Applied == 1 && status.State == L10n.T("Правило применено") && status.Detail.Contains("Windows CPU Sets 0x"),
                        "Fortnite's matched process is confirmed using Windows CPU Sets");
                    Check(CpuSets.Matches(handle, CpuSets.Select(handle, chosen)) && Native.ReadMask(handle) == hardMask && Native.ReadPriority(handle) == priority,
                        "Windows confirms selected default CPU Sets independently of hard affinity and priority");
                    CpuSets.Apply(handle, Array.Empty<uint>());
                    config.Paused = true;
                    engine.Scan(config);
                    Check(CpuSets.Matches(handle, Array.Empty<uint>()), "pause leaves externally cleared CPU Sets alone");
                    config.Paused = false;
                    engine.Scan(config);
                    Check(CpuSets.Matches(handle, CpuSets.Select(handle, chosen)), "resuming restores the selected default CPU Sets");
                    profile.Mask = hardMask.ToString("X");
                    engine.Scan(config);
                    Check(CpuSets.Matches(handle, CpuSets.Select(handle, hardMask)), "choosing all allowed CPUs replaces the previous narrower CPU Sets");
                }
            }
            finally { if (!game.HasExited) { game.Kill(); game.WaitForExit(); } }
        }
    }
}
