using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace NumaControl
{
    internal static partial class Tests
    {
        private static ulong OwnMask() { using (Process p = Process.GetCurrentProcess()) using (var handle = Native.Open(p.Id, false)) return Native.ReadMask(handle); }
        private static void SelfAffinityChecks(Topology cpu, string fixturePath)
        {
            bool fallback;
            Check(SelfAffinity.Choose(cpu, 0, out fallback) == cpu.AllMask && !fallback, "self affinity: no game leaves all CPUs available");
            Check(SelfAffinity.Choose(cpu, cpu.AllMask, out fallback) == cpu.Cores.Last().Mask && fallback, "self affinity: all CPUs used selects last physical core");
            foreach (CpuCore core in cpu.Cores)
            {
                ulong game = cpu.AllMask & ~core.Mask;
                Check(SelfAffinity.Choose(cpu, game, out fallback) == core.Mask && !fallback, "self affinity: choose excluded physical core " + core.Index);
                ulong oneSibling = 1UL << Topology.FirstBit(core.Mask), chosen = SelfAffinity.Choose(cpu, oneSibling, out fallback);
                if (cpu.Cores.Count > 1) Check((chosen & core.Mask) == 0 && !fallback, "self affinity: SMT sibling excluded with occupied core " + core.Index);
            }
            string name = Path.GetFileNameWithoutExtension(fixturePath); ulong original = OwnMask();
            Configuration config = new Configuration(); GameProfile profile = new GameProfile { Name = "Own affinity fixture", ProcessName = name, ExePath = fixturePath, LaunchTarget = fixturePath, Enabled = true, Mask = cpu.LegacyMask().ToString("X") }; config.Games.Add(profile);
            using (WatchEngine engine = new WatchEngine(config, cpu, new AppLog(Path.Combine(root, "self-affinity-log")), true))
            {
                Process game = null;
                try
                {
                    engine.Start(); Check(Until(() => engine.AppAffinity.ActiveGames == 0, 2000), "self affinity: watcher starts without restricting idle process");
                    using (Process own = Process.GetCurrentProcess()) using (var handle = Native.Open(own.Id, true)) Native.Apply(handle, cpu.Cores.Last().Mask);
                    GameLauncher.Launch(profile, cpu.AllMask); game = Process.GetProcessesByName(name).Single();
                    using (var handle = Native.Open(game.Id, false)) Check(Native.ReadMask(handle) == profile.CpuMask, "direct launch clears inherited application affinity immediately");
                    engine.Refresh(); ulong expected = SelfAffinity.Choose(cpu, profile.CpuMask, out fallback);
                    Check(Until(() => OwnMask() == expected && engine.AppAffinity.ActiveGames == 1, 4000), "self affinity: actual application process moves outside game CPUs");
                    Configuration changed = config.Copy(); changed.Games[0].Mask = cpu.AllMask.ToString("X"); engine.Update(changed);
                    Check(Until(() => OwnMask() == cpu.Cores.Last().Mask && engine.AppAffinity.Fallback, 4000), "self affinity: full game profile triggers last-core fallback at runtime");
                    using (var handle = Native.Open(game.Id, false)) Check(Native.ReadMask(handle) == cpu.AllMask, "last-core fallback never changes full game profile");
                    changed.Paused = true; engine.Update(changed); using (var handle = Native.Open(game.Id, true)) Native.Apply(handle, profile.CpuMask); engine.Refresh();
                    Check(Until(() => OwnMask() == expected && !engine.AppAffinity.Fallback, 4000), "self affinity: paused game watcher still follows actual running game mask");
                    changed.Games[0].Enabled = false; engine.Update(changed); Check(Until(() => engine.AppAffinity.ActiveGames == 1 && OwnMask() == expected, 3000), "self affinity: disabled game enforcement still preserves application separation");
                    changed.Games.Clear(); engine.Update(changed); engine.Refresh(); Check(Until(() => engine.Snapshot().Count == 0 && engine.AppAffinity.ActiveGames == 1 && OwnMask() == expected, 3000), "self affinity: deleted profile remains tracked until its running game exits");
                    game.Kill(); game.WaitForExit(); engine.Refresh(); Check(Until(() => OwnMask() == original && engine.AppAffinity.ActiveGames == 0, 4000), "self affinity: last game exit restores original application mask");
                }
                finally { if (game != null) { if (!game.HasExited) { game.Kill(); game.WaitForExit(); } game.Dispose(); } }
            }
            Check(OwnMask() == original, "self affinity: watcher disposal restores original process mask");
            foreach (string uri in new[] { "steam://rungameid/730", "steam://rungameid/570", "astrumplay://play/0.1177" })
            {
                ProcessStartInfo sent = null; GameProfile viaLauncher = new GameProfile { Name = "URI test", ProcessName = "NumaMissingUriFixture", LaunchTarget = uri };
                GameLauncher.Launch(viaLauncher, cpu.AllMask, info => { sent = info; return null; });
                Check(sent != null && sent.FileName == uri && sent.UseShellExecute, "launcher protocol dispatches exact URI through Windows Shell: " + uri);
            }
            ResourceSample failure = Healthy(0); failure.AppAffinityError = true;
            Check(new HealthPolicy(new HealthLimits()).Observe(failure) == "app-affinity-failure", "self affinity assignment failure is detected by diagnostics");
        }
    }
}
