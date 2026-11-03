using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;

namespace NumaControl
{
    internal sealed class AppAffinityState
    {
        internal ulong Mask, GamesMask;
        internal int ActiveGames;
        internal bool Fallback, Error;
        internal string Detail;
        internal string Signature { get { return Mask + "|" + GamesMask + "|" + ActiveGames + "|" + Detail; } }
    }
    internal sealed class SelfAffinity : IDisposable
    {
        private readonly Topology topology;
        private readonly int ownPid;
        private readonly ulong original;
        private volatile AppAffinityState state;
        private readonly Dictionary<int, GameProfile> tracked = new Dictionary<int, GameProfile>();
        internal AppAffinityState State { get { return state; } }
        internal SelfAffinity(Topology cpu)
        {
            topology = cpu;
            using (Process own = Process.GetCurrentProcess()) ownPid = own.Id;
            using (var handle = Native.Open(ownPid, false)) original = Native.ReadMask(handle);
            state = new AppAffinityState { Mask = original, Detail = L10n.T("Ожидание первой проверки игр.") };
        }
        internal static ulong Choose(Topology cpu, ulong gamesMask, out bool fallback)
        {
            ulong occupied = 0;
            foreach (CpuCore core in cpu.Cores) if ((core.Mask & gamesMask) != 0) occupied |= core.Mask;
            ulong free = cpu.AllMask & ~occupied;
            fallback = free == 0;
            return fallback ? cpu.Cores[cpu.Cores.Count - 1].Mask : free;
        }
        internal void Refresh(Configuration config, Dictionary<string, Process[]> processes, int session, ProcessSnapshot snapshot)
        {
            ulong used = 0; int active = 0; bool unreadable = false;
            HashSet<int> seen = new HashSet<int>();
            var targets = config.Games.Select(g => new KeyValuePair<int, GameProfile>(0, g)).Concat(tracked.ToArray()).ToArray();
            foreach (var trackedTarget in targets)
            {
                GameProfile game = trackedTarget.Value;
                Process[] candidates;
                if (!processes.TryGetValue(game.ProcessName, out candidates)) { candidates = snapshot.Find(game.ProcessName); processes.Add(game.ProcessName, candidates); }
                foreach (Process process in candidates)
                {
                    try
                    {
                        if ((trackedTarget.Key != 0 && process.Id != trackedTarget.Key) || process.Id == ownPid || seen.Contains(process.Id) || ProcessSnapshot.Session(process.Id) != session) continue;
                        using (var handle = Native.Open(process.Id, false))
                        {
                            if (!game.Matches(Native.Image(handle))) continue;
                            used |= Native.ReadMask(handle); active++; seen.Add(process.Id); tracked[process.Id] = game;
                        }
                    }
                    catch (InvalidOperationException) { }
                    catch (Win32Exception)
                    {
                        bool exited; try { exited = process.HasExited; } catch (InvalidOperationException) { exited = true; } catch (Win32Exception) { exited = false; }
                        if (!exited) { used |= topology.AllMask; unreadable = true; if (seen.Add(process.Id)) active++; }
                    }
                }
            }
            foreach (int pid in tracked.Keys.Where(pid => !seen.Contains(pid)).ToArray()) tracked.Remove(pid);
            bool fallback = false;
            ulong target = active == 0 ? original : Choose(topology, used, out fallback);
            try
            {
                using (var handle = Native.Open(ownPid, true)) Native.Apply(handle, target);
                state = new AppAffinityState { Mask = target, GamesMask = used, ActiveGames = active, Fallback = fallback,
                    Detail = active == 0 ? L10n.T("Игры не запущены · исходная привязка приложения.") : unreadable ? L10n.T("Маска игры недоступна · резервное последнее физическое ядро.") : fallback ? L10n.T("Резерв: последнее физическое ядро. Есть пересечение с маской игры.") : L10n.T("Физические ядра вне масок запущенных игр, включая исключение SMT-соседей.") };
            }
            catch (Win32Exception error) { state = new AppAffinityState { Mask = target, GamesMask = used, ActiveGames = active, Error = true, Detail = L10n.T("Не удалось назначить ядра приложению: ") + error.Message }; }
        }
        internal static string CpuList(ulong mask) { return string.Join(", ", Enumerable.Range(0, 64).Where(i => (mask & (1UL << i)) != 0)); }
        public void Dispose() { try { using (var handle = Native.Open(ownPid, true)) Native.Apply(handle, original); } catch (Win32Exception) { } catch (InvalidOperationException) { } }
    }
}
