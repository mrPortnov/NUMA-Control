using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace NumaControl
{
    internal static class Native
    {
        internal const uint Query = 0x1000, Set = 0x0200;
        [DllImport("kernel32.dll")] internal static extern ushort GetActiveProcessorGroupCount();
        [DllImport("kernel32.dll")] internal static extern uint GetActiveProcessorCount(ushort group);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool QueryFullProcessImageName(SafeProcessHandle handle, uint flags, StringBuilder path, ref uint size);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetProcessAffinityMask(SafeProcessHandle handle, out UIntPtr mask, out UIntPtr system);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool SetProcessAffinityMask(SafeProcessHandle handle, UIntPtr mask);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetPriorityClass(SafeProcessHandle handle);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetPriorityClass(SafeProcessHandle handle, uint priority);
        internal static uint ReadPriority(SafeProcessHandle handle) { uint value = GetPriorityClass(handle); if (value == 0) throw new Win32Exception(Marshal.GetLastWin32Error()); return value; }
        internal static bool ApplyHighPriority(SafeProcessHandle handle)
        {
            if (ReadPriority(handle) == 0x80) return false;
            if (!SetPriorityClass(handle, 0x80)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (ReadPriority(handle) != 0x80) throw new InvalidOperationException(L10n.T("Windows не сохранила высокий приоритет."));
            return true;
        }
        [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr window, int command);
        internal static SafeProcessHandle Open(int pid, bool writable)
        {
            return Open(pid, Query | (writable ? Set : 0));
        }
        internal static SafeProcessHandle Open(int pid, uint access)
        {
            SafeProcessHandle handle = OpenProcess(access, false, pid);
            if (!handle.IsInvalid) return handle;
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        internal static string Image(SafeProcessHandle handle)
        {
            StringBuilder path = new StringBuilder(512);
            uint size = (uint)path.Capacity;
            if (!QueryFullProcessImageName(handle, 0, path, ref size))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != 122) throw new Win32Exception(error);
                path = new StringBuilder(32768); size = (uint)path.Capacity;
                if (!QueryFullProcessImageName(handle, 0, path, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return Path.GetFullPath(path.ToString());
        }
        internal static ulong ReadMask(SafeProcessHandle handle)
        {
            UIntPtr current, system;
            if (!GetProcessAffinityMask(handle, out current, out system)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return current.ToUInt64();
        }
        internal static bool Apply(SafeProcessHandle handle, ulong mask)
        {
            if (ReadMask(handle) == mask) return false;
            if (!SetProcessAffinityMask(handle, new UIntPtr(mask))) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (ReadMask(handle) != mask) throw new InvalidOperationException(L10n.T("Windows не сохранила выбранную привязку."));
            return true;
        }
    }

    internal sealed class CpuCore
    {
        internal int Index;
        internal ushort Group;
        internal ulong Mask;
        internal int Efficiency;
    }
    internal sealed class Topology
    {
        internal int Count, Groups;
        internal ulong AllMask;
        internal string Name;
        internal string Error;
        internal readonly List<CpuCore> Cores = new List<CpuCore>();
        internal bool Supported { get { return Error == null; } }
        internal static ulong FullMask(int count)
        {
            if (count < 1 || count > 64) throw new ArgumentOutOfRangeException("count");
            return count == 64 ? ulong.MaxValue : (1UL << count) - 1;
        }
        internal static Topology Detect()
        {
            Topology t = new Topology();
            t.Name = L10n.T("Процессор Windows");
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                if (key != null) t.Name = ((string)key.GetValue("ProcessorNameString", t.Name)).Trim();
            t.Groups = Native.GetActiveProcessorGroupCount();
            t.Count = checked((int)Native.GetActiveProcessorCount(0xffff));
            if (IntPtr.Size != 8 || t.Groups != 1 || t.Count < 1 || t.Count > 64)
                t.Error = L10n.T("Этот CPU использует несколько групп процессоров. Его топология определена, но изменение привязки более 64 потоков пока не поддерживается.");
            uint size = 0;
            Native.GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref size);
            if (size == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr buffer = Marshal.AllocHGlobal(checked((int)size));
            try
            {
                if (!Native.GetLogicalProcessorInformationEx(0, buffer, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
                for (int offset = 0; offset < size; )
                {
                    IntPtr entry = IntPtr.Add(buffer, offset);
                    int bytes = Marshal.ReadInt32(entry, 4);
                    if (bytes < 48 || offset + bytes > size) throw new InvalidDataException(L10n.T("Некорректная топология процессора."));
                    // Header 8 + PROCESSOR_RELATIONSHIP.GroupCount at 22; GROUP_AFFINITY starts at 24.
                    ushort groups = (ushort)Marshal.ReadInt16(entry, 30);
                    if (groups < 1 || bytes < 32 + groups * 16) throw new InvalidDataException(L10n.T("Некорректные группы топологии."));
                    for (int group = 0; group < groups; group++)
                    {
                        ulong mask = unchecked((ulong)Marshal.ReadInt64(entry, 32 + group * 16));
                        ushort groupId = (ushort)Marshal.ReadInt16(entry, 40 + group * 16);
                        t.Cores.Add(new CpuCore { Mask = mask, Group = groupId, Efficiency = Marshal.ReadByte(entry, 9) });
                        if (groupId == 0) t.AllMask |= mask;
                    }
                    offset += bytes;
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
            t.Cores.Sort(delegate(CpuCore a, CpuCore b) { int group = a.Group.CompareTo(b.Group); return group != 0 ? group : FirstBit(a.Mask).CompareTo(FirstBit(b.Mask)); });
            for (int i = 0; i < t.Cores.Count; i++) t.Cores[i].Index = i;
            if (t.Supported && (t.AllMask == 0 || Bits(t.AllMask) != t.Count)) throw new InvalidDataException(L10n.T("Топология не соответствует числу активных ЦП."));
            return t;
        }
        internal static int FirstBit(ulong mask) { for (int i = 0; i < 64; i++) if ((mask & (1UL << i)) != 0) return i; return 64; }
        internal static int Bits(ulong mask) { int n = 0; while (mask != 0) { mask &= mask - 1; n++; } return n; }
        internal ulong LegacyMask() { return Count > 4 ? AllMask & ~(3UL | (3UL << (Count - 2))) : AllMask; }
        internal ulong RecommendedMask() { return WithoutEdgeCores(); }
        internal ulong WithoutEdgeCores()
        {
            return Cores.Count > 2 ? AllMask & ~Cores[0].Mask & ~Cores[Cores.Count - 1].Mask : AllMask;
        }
        internal ulong OnePerCore()
        {
            ulong mask = 0;
            foreach (CpuCore core in Cores) mask |= 1UL << FirstBit(core.Mask);
            return mask;
        }
    }

    public sealed class GameProfile
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string ProcessName { get; set; }
        public string ExePath { get; set; }
        public string LaunchTarget { get; set; }
        public string Arguments { get; set; }
        public string LaunchDirectory { get; set; }
        public bool LaunchViaShell { get; set; }
        public string Mask { get; set; }
        public bool Enabled { get; set; }
        public bool HighPriority { get; set; }
        public string Color { get; set; }
        public GameProfile() { Id = Guid.NewGuid().ToString("N"); Name = L10n.T("Новая игра"); ProcessName = ""; ExePath = ""; LaunchTarget = ""; Arguments = ""; Mask = "1"; Color = "#8C83FF"; HighPriority = true; }
        internal ulong CpuMask { get { return ulong.Parse(Mask, NumberStyles.HexNumber, CultureInfo.InvariantCulture); } }
        internal GameProfile Copy() { return (GameProfile)MemberwiseClone(); }
        internal bool Matches(string image)
        {
            if (!string.Equals(Path.GetFileNameWithoutExtension(image), ProcessName, StringComparison.OrdinalIgnoreCase)) return false;
            return string.IsNullOrWhiteSpace(ExePath) || string.Equals(Path.GetFullPath(image), Path.GetFullPath(ExePath), StringComparison.OrdinalIgnoreCase);
        }
    }
    public sealed class Configuration
    {
        internal const int MaxGames = 100;
        public int Version { get; set; }
        public int IdleIntervalMs { get; set; }
        public int ActiveIntervalMs { get; set; }
        public bool Paused { get; set; }
        public string Theme { get; set; }
        public string Language { get; set; }
        public List<GameProfile> Games { get; set; }
        public Configuration() { Version = 1; IdleIntervalMs = 2000; ActiveIntervalMs = 2000; Theme = "standard"; Language = "ru"; Games = new List<GameProfile>(); }
        internal Configuration Copy()
        {
            Configuration copy = (Configuration)MemberwiseClone();
            copy.Games = Games.Select(g => g.Copy()).ToList();
            return copy;
        }
        internal static Configuration Defaults(Topology t)
        {
            return new Configuration();
        }
        internal void Validate(Topology t, bool requireCompatibleMasks)
        {
            if (Version != 1 || Games == null) throw new InvalidDataException(L10n.T("Неподдерживаемый файл профилей (версия 1)."));
            if (!Themes.Valid(Theme)) throw new InvalidDataException(L10n.T("Неизвестная тема оформления."));
            if (!L10n.Valid(Language)) throw new InvalidDataException(L10n.T("Неизвестный язык интерфейса."));
            if (Games.Count > MaxGames) throw new InvalidDataException(L10n.T("Достигнут предел ") + MaxGames + L10n.T(" профилей. Удалите ненужный профиль перед добавлением нового."));
            if (IdleIntervalMs < 1000 || IdleIntervalMs > 10000 || ActiveIntervalMs < 500 || ActiveIntervalMs > 10000) throw new InvalidDataException(L10n.T("Интервал проверки вне допустимого диапазона."));
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (GameProfile g in Games)
            {
                Guid id;
                if (g == null || !Guid.TryParseExact(g.Id, "N", out id) || !ids.Add(g.Id)) throw new InvalidDataException(L10n.T("Некорректный или повторяющийся идентификатор игры."));
                if (string.IsNullOrWhiteSpace(g.Name) || g.Name.Length > 80) throw new InvalidDataException(L10n.T("Укажите название игры (до 80 символов)."));
                if (string.IsNullOrWhiteSpace(g.ProcessName) || g.ProcessName != g.ProcessName.Trim() || g.ProcessName == "." || g.ProcessName == ".." || g.ProcessName.Length > 200 || g.ProcessName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || g.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(L10n.T("Имя процесса должно быть без пути и расширения .exe."));
                ulong mask;
                if (g.Mask == null || g.Mask.Length > 16 || !ulong.TryParse(g.Mask, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out mask) || mask == 0) throw new InvalidDataException(L10n.T("Выберите хотя бы один логический процессор для «") + g.Name + "».");
                if (requireCompatibleMasks && t.Supported && (mask & ~t.AllMask) != 0) throw new InvalidDataException(L10n.T("Профиль «") + g.Name + L10n.T("» использует ЦП, которых нет на этом компьютере. Выберите ядра заново."));
                if (!string.IsNullOrWhiteSpace(g.ExePath))
                {
                    ValidateExe(g.ExePath);
                    if (!string.Equals(Path.GetFileNameWithoutExtension(g.ExePath), g.ProcessName, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(L10n.T("Имя процесса не совпадает с именем EXE."));
                }
                ValidateLaunch(g.LaunchTarget);
                ValidateLaunchArguments(g.Arguments);
                LauncherTargets.ValidateDirectory(g.LaunchDirectory);
                if (g.Color == null || !System.Text.RegularExpressions.Regex.IsMatch(g.Color, "^#[0-9a-fA-F]{6}$")) g.Color = "#8C83FF";
            }
            for (int i = 0; i < Games.Count; i++) for (int j = i + 1; j < Games.Count; j++)
                if (string.Equals(Games[i].ProcessName, Games[j].ProcessName, StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrWhiteSpace(Games[i].ExePath) || string.IsNullOrWhiteSpace(Games[j].ExePath) || string.Equals(Games[i].ExePath, Games[j].ExePath, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException(L10n.T("Профили «") + Games[i].Name + L10n.T("» и «") + Games[j].Name + L10n.T("» пересекаются. Укажите разные полные пути EXE."));
        }
        internal static void ValidateExe(string path)
        {
            if (!Path.IsPathRooted(path) || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) || path.IndexOf('"') >= 0 || Path.GetFullPath(path) != path) throw new InvalidDataException(L10n.T("Укажите полный путь к файлу .exe."));
        }
        internal static bool IsGameUri(string target)
        {
            return target != null && (LauncherTargets.IsClientUri(target) || System.Text.RegularExpressions.Regex.IsMatch(target, @"\A(?:steam://rungameid/[0-9]+|astrumplay://play/[0-9.]+)\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant));
        }
        internal static void ValidateLaunch(string target) { if (!string.IsNullOrWhiteSpace(target) && !IsGameUri(target)) ValidateExe(target); }
        internal static void ValidateLaunchArguments(string arguments)
        {
            if (arguments != null && (arguments.Length > 4096 || arguments.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0))
                throw new InvalidDataException(L10n.T("Некорректные аргументы запуска."));
        }
    }

    internal sealed class ProfileStore
    {
        internal readonly string DirectoryPath;
        internal string FilePath { get { return Path.Combine(DirectoryPath, "profiles.json"); } }
        internal ProfileStore(string directory) { DirectoryPath = directory; }
        internal static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = 1024 * 1024, RecursionLimit = 32 }; }
        internal static Configuration Read(string path, Topology t, bool strict)
        {
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException(L10n.T("Файл профилей больше 1 МиБ."));
            string text = File.ReadAllText(path, Encoding.UTF8);
            var fields = Serializer().DeserializeObject(text) as Dictionary<string, object>;
            if (fields == null || !fields.ContainsKey("Version") || !fields.ContainsKey("Games") || !fields.ContainsKey("IdleIntervalMs") || !fields.ContainsKey("ActiveIntervalMs") || !fields.ContainsKey("Paused")) throw new InvalidDataException(L10n.T("В файле отсутствуют обязательные поля профилей."));
            Configuration c = Serializer().Deserialize<Configuration>(text);
            if (c == null) throw new InvalidDataException(L10n.T("Пустой файл профилей."));
            // Older profiles did not request priority management. Preserve their behavior on upgrade.
            var rawGames = fields["Games"] as object[];
            if (rawGames != null && c.Games != null)
                for (int i = 0; i < Math.Min(rawGames.Length, c.Games.Count); i++)
                { var raw = rawGames[i] as Dictionary<string, object>; if (raw != null && !raw.ContainsKey("HighPriority") && c.Games[i] != null) c.Games[i].HighPriority = false; }
            c.Validate(t, strict);
            return c;
        }
        internal Configuration Load(Topology t) { return File.Exists(FilePath) ? Read(FilePath, t, false) : Configuration.Defaults(t); }
        internal void PreserveRecoveryFiles()
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            if (File.Exists(FilePath)) File.Copy(FilePath, Path.Combine(DirectoryPath, "profiles.corrupt-" + stamp + ".json"));
            if (File.Exists(FilePath + ".bak")) File.Copy(FilePath + ".bak", Path.Combine(DirectoryPath, "profiles.recovery-" + stamp + ".json"));
        }
        internal void Save(Configuration config)
        {
            Directory.CreateDirectory(DirectoryPath);
            WriteAtomic(FilePath, Serializer().Serialize(config));
        }
        internal static void WriteAtomic(string path, string contents)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream f = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] data = new UTF8Encoding(false).GetBytes(contents);
                    f.Write(data, 0, data.Length);
                    f.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
    internal sealed class AppLog
    {
        private readonly object gate = new object();
        internal readonly string PathName;
        internal AppLog(string directory) { PathName = Path.Combine(directory, "watcher.log"); }
        internal void Write(string text)
        {
            lock (gate)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(PathName));
                    if (File.Exists(PathName) && new FileInfo(PathName).Length > 1024 * 1024)
                    {
                        if (File.Exists(PathName + ".previous")) File.Delete(PathName + ".previous");
                        File.Move(PathName, PathName + ".previous");
                    }
                    File.AppendAllText(PathName, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + text + Environment.NewLine, Encoding.UTF8);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        internal string Tail()
        {
            lock (gate)
            {
                if (!File.Exists(PathName)) return L10n.T("Журнал пока пуст. Здесь появятся изменения состояния и ошибки.");
                string[] lines = File.ReadAllLines(PathName, Encoding.UTF8);
                return string.Join(Environment.NewLine, lines.Skip(Math.Max(0, lines.Length - 250)).Reverse());
            }
        }
    }
    internal sealed class GameStatus
    {
        internal string Id, State, Detail;
        internal int Running, Applied;
        internal bool Error;
        internal List<int> Pids = new List<int>();
        internal string Signature { get { return State + "|" + Detail; } }
    }
    internal sealed class WatchEngine : IDisposable
    {
        private readonly object gate = new object();
        private Configuration config;
        private readonly Topology topology;
        private readonly AppLog log;
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly Thread thread;
        private volatile bool stopping;
        private long heartbeat = Stopwatch.GetTimestamp();
        private int consecutiveErrors;
        private long lastScanTicks, cycles;
        internal double LastScanMilliseconds { get { return Interlocked.Read(ref lastScanTicks) * 1000.0 / Stopwatch.Frequency; } }
        internal long CycleCount { get { return Interlocked.Read(ref cycles); } }
        private int knownThreads;
        internal int KnownThreads { get { return Volatile.Read(ref knownThreads); } }
        internal bool Alive { get { return thread.IsAlive; } }
        internal long Heartbeat { get { return Interlocked.Read(ref heartbeat); } }
        internal int ConsecutiveErrors { get { return Volatile.Read(ref consecutiveErrors); } }
        private readonly int session;
        private readonly SelfAffinity selfAffinity;
        internal AppAffinityState AppAffinity { get { return selfAffinity == null ? null : selfAffinity.State; } }
        private Dictionary<string, GameStatus> statuses = new Dictionary<string, GameStatus>();
        internal event Action Changed;
        internal WatchEngine(Configuration c, Topology t, AppLog l, bool manageOwnAffinity = false)
        {
            config = c.Copy(); topology = t; log = l;
            using (Process self = Process.GetCurrentProcess()) session = self.SessionId;
            if (manageOwnAffinity && t.Supported) selfAffinity = new SelfAffinity(t);
            thread = new Thread(Run) { IsBackground = true, Name = "Affinity monitor", Priority = ThreadPriority.BelowNormal };
        }
        internal void Start() { thread.Start(); }
        internal void Update(Configuration c) { lock (gate) config = c.Copy(); wake.Set(); }
        internal void Refresh() { wake.Set(); }
        internal Dictionary<string, GameStatus> Snapshot() { lock (gate) return new Dictionary<string, GameStatus>(statuses); }
        private void Run()
        {
            while (!stopping)
            {
                Interlocked.Exchange(ref heartbeat, Stopwatch.GetTimestamp());
                Configuration c;
                lock (gate) c = config.Copy();
                int delay = c.IdleIntervalMs;
                try
                {
                    string previousAffinity = selfAffinity == null ? null : selfAffinity.State.Signature;
                    long scanStart = Stopwatch.GetTimestamp();
                    Dictionary<string, GameStatus> next = Scan(c);
                    Interlocked.Exchange(ref lastScanTicks, Stopwatch.GetTimestamp() - scanStart); Interlocked.Increment(ref cycles);
                    bool changed;
                    lock (gate)
                    {
                        changed = next.Count != statuses.Count || next.Any(p => !statuses.ContainsKey(p.Key) || statuses[p.Key].Signature != p.Value.Signature) || (selfAffinity != null && selfAffinity.State.Signature != previousAffinity);
                        if (changed)
                            foreach (var pair in next)
                                if (!statuses.ContainsKey(pair.Key) || statuses[pair.Key].Signature != pair.Value.Signature) log.Write(pair.Value.State + " | " + c.Games.First(g => g.Id == pair.Key).Name + " | " + pair.Value.Detail);
                        statuses = next;
                    }
                    if (next.Values.Any(s => s.Running > 0)) delay = c.ActiveIntervalMs;
                    if (((c.Paused || !c.Games.Any(g => g.Enabled)) && (selfAffinity == null || (c.Games.Count == 0 && AppAffinity.ActiveGames == 0))) || !topology.Supported) delay = 30000;
                    Interlocked.Exchange(ref consecutiveErrors, 0);
                    Interlocked.Exchange(ref heartbeat, Stopwatch.GetTimestamp());
                    if (changed && Changed != null) Changed();
                }
                catch (Exception error) { Interlocked.Increment(ref consecutiveErrors); log.Write(L10n.T("Ошибка цикла: ") + error.Message); delay = 10000; }
                wake.WaitOne(delay);
            }
        }
        internal Dictionary<string, GameStatus> Scan(Configuration c)
        {
            Dictionary<string, GameStatus> next = new Dictionary<string, GameStatus>();
            Dictionary<string, Process[]> processes = new Dictionary<string, Process[]>(StringComparer.OrdinalIgnoreCase);
            ProcessSnapshot snapshot = new ProcessSnapshot();
            Volatile.Write(ref knownThreads, snapshot.OwnThreads);
            try
            {
                foreach (GameProfile game in c.Games)
                {
                    GameStatus status = new GameStatus { Id = game.Id, State = L10n.T("Ожидание игры"), Detail = L10n.T("Наблюдатель включён · ") + game.ProcessName + ".exe" };
                    next.Add(game.Id, status);
                    if (!topology.Supported) { status.State = L10n.T("Система не поддерживается"); status.Detail = topology.Error; status.Error = true; continue; }
                    if (c.Paused || !game.Enabled) { status.State = c.Paused ? L10n.T("Общая пауза") : L10n.T("Наблюдатель выключен"); status.Detail = L10n.T("Привязка запущенной игры сохраняется до её закрытия."); continue; }
                    if ((game.CpuMask & ~topology.AllMask) != 0) { status.State = L10n.T("Проверьте ядра"); status.Detail = L10n.T("В профиле есть ЦП, недоступные на этом компьютере."); status.Error = true; continue; }
                    Process[] candidates;
                    if (!processes.TryGetValue(game.ProcessName, out candidates)) { candidates = snapshot.Find(game.ProcessName); processes.Add(game.ProcessName, candidates); }
                    bool useCpuSets = CpuSets.IsUsedForProcess(game.ProcessName);
                    List<string> errors = new List<string>();
                    foreach (Process process in candidates)
                    {
                        string operation = L10n.T("проверка процесса");
                        try
                        {
                            if (ProcessSnapshot.Session(process.Id) != session) continue;
                            // Query-only checks on the common path. Ask for set rights only when a change is needed.
                            using (SafeProcessHandle handle = Native.Open(process.Id, false))
                            {
                                if (!game.Matches(Native.Image(handle))) continue;
                                status.Running++;
                                status.Pids.Add(process.Id);
                                if (useCpuSets)
                                {
                                    operation = L10n.T("проверка набора CPU Sets");
                                    uint[] ids = CpuSets.Select(handle, game.CpuMask);
                                    if (!CpuSets.Matches(handle, ids))
                                    {
                                        operation = L10n.T("назначение набора CPU Sets");
                                        using (SafeProcessHandle writable = Native.Open(process.Id, Native.Query | CpuSets.SetLimited))
                                        {
                                            if (!game.Matches(Native.Image(writable))) continue;
                                            CpuSets.Apply(writable, ids);
                                            log.Write(game.Name + " · PID " + process.Id + " → Windows CPU Sets 0x" + game.Mask);
                                        }
                                    }
                                }
                                else
                                {
                                    operation = L10n.T("чтение маски ЦП");
                                    if (Native.ReadMask(handle) != game.CpuMask)
                                    {
                                        operation = L10n.T("назначение маски ЦП");
                                        using (SafeProcessHandle writable = Native.Open(process.Id, true))
                                        {
                                            if (!game.Matches(Native.Image(writable))) continue;
                                            if (Native.Apply(writable, game.CpuMask))
                                                log.Write(game.Name + " · PID " + process.Id + " → 0x" + game.Mask);
                                        }
                                    }
                                }
                                status.Applied++;
                                if (game.HighPriority)
                                {
                                    operation = L10n.T("чтение приоритета");
                                    if (Native.ReadPriority(handle) != 0x80)
                                    {
                                        operation = L10n.T("назначение высокого приоритета");
                                        using (SafeProcessHandle writable = Native.Open(process.Id, true))
                                        {
                                            if (game.Matches(Native.Image(writable)) && Native.ApplyHighPriority(writable))
                                                log.Write(game.Name + " · PID " + process.Id + L10n.T(" → высокий приоритет"));
                                        }
                                    }
                                }
                            }
                        }
                        catch (InvalidOperationException error) { errors.Add(error.Message); }
                        catch (Win32Exception error)
                        {
                            bool exited;
                            try { exited = process.HasExited; } catch (InvalidOperationException) { exited = true; } catch (Win32Exception) { exited = false; }
                            if (exited) continue;
                            errors.Add("PID " + process.Id + " · " + operation + " · " +
                                (error.NativeErrorCode == 5 ? L10n.T("Windows отказала в доступе (код 5).") : error.Message));
                        }
                    }
                    if (errors.Count > 0)
                    {
                        status.State = L10n.T("Нужна проверка");
                        status.Detail = (status.Applied > 0 ? L10n.T(useCpuSets ? "CPU Sets подтверждены для процессов: " : "Маска ЦП подтверждена для процессов: ") + status.Applied + ". " : "") + string.Join(" ", errors.Distinct());
                        status.Error = true;
                    }
                    else if (status.Running > 0)
                    {
                        status.State = L10n.T("Правило применено");
                        status.Detail = "PID " + string.Join(", ", status.Pids) + (useCpuSets ? " · Windows CPU Sets 0x" : L10n.T(" · маска 0x")) +
                            game.Mask + (game.HighPriority ? L10n.T(" · высокий приоритет") : "") + L10n.T(" · автоматическое восстановление");
                    }
                }
                if (selfAffinity != null) selfAffinity.Refresh(c, processes, session, snapshot);
            }
            finally { foreach (Process[] items in processes.Values) foreach (Process item in items) item.Dispose(); }
            return next;
        }
        internal void RequestStop() { stopping = true; wake.Set(); }
        public void Dispose() { RequestStop(); if (thread.IsAlive && !thread.Join(3000)) return; if (selfAffinity != null) selfAffinity.Dispose(); wake.Dispose(); }
    }
}
