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

            string install = Path.Combine(folder, "Fortnite");
            string binary = Path.Combine(install, @"FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(binary));
            File.Copy(fixture, binary);
            File.WriteAllText(Path.Combine(folder, "broken.item"), "{");
            File.WriteAllText(Path.Combine(folder, "Fortnite.item"), ProfileStore.Serializer().Serialize(new {
                AppName = "Fortnite", InstallLocation = install, CatalogNamespace = "fn", CatalogItemId = "example",
                LaunchExecutable = "FortniteGame/Binaries/Win64/FortniteBootstrapper.exe"
            }));
            GameProfile fortnite = LauncherTargets.FindFortnite(folder);
            Check(fortnite != null && fortnite.ExePath == binary && fortnite.ProcessName == "FortniteClient-Win64-Shipping" && fortnite.LaunchTarget == epic,
                "Fortnite uses the game client for matching and the Epic URI for launching");
        }
    }
}
