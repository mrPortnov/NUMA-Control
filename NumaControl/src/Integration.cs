using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace NumaControl
{
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    internal class ShellLinkObject { }
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr data, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int max);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int command);
        void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder icon, int max, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string icon, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
    internal static class ShellLinks
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern void SHChangeNotify(uint eventId, uint flags, string item, IntPtr second);
        internal static string IconOverride;
        internal static string Executable { get { return Assembly.GetExecutingAssembly().Location; } }
        internal static string StartupPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "NUMA Control.lnk"); } }
        internal static void Write(string path, string target, string arguments, string description)
        {
            object link = null;
            try
            {
                link = new ShellLinkObject();
                IShellLinkW shortcut = (IShellLinkW)link;
                shortcut.SetPath(target);
                shortcut.SetArguments(arguments);
                shortcut.SetWorkingDirectory(Path.GetDirectoryName(target));
                shortcut.SetDescription(description);
                shortcut.SetIconLocation(string.Equals(target, Executable, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(IconOverride) ? IconOverride : target, 0);
                ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Save(path, true);
            }
            finally
            {
                if (link != null) Marshal.FinalReleaseComObject(link);
            }
        }
        internal static string Target(string path)
        {
            if (!File.Exists(path)) return "";
            object link = null;
            try
            {
                link = new ShellLinkObject();
                ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Load(path, 0);
                StringBuilder target = new StringBuilder(32768);
                ((IShellLinkW)link).GetPath(target, target.Capacity, IntPtr.Zero, 0);
                return target.ToString();
            }
            finally
            {
                if (link != null) Marshal.FinalReleaseComObject(link);
            }
        }
        internal static void UpdateOwnedIcons(string directory, string executable, string icon)
        {
            if (!Directory.Exists(directory)) return;
            foreach (string file in Directory.GetFiles(directory, "*.lnk")) UpdateIcon(file, executable, icon);
        }
        internal static void UpdateIcon(string path, string executable, string icon)
        {
            if (!string.Equals(Target(path), executable, StringComparison.OrdinalIgnoreCase)) return;
            object link = null;
            try { link = new ShellLinkObject(); ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Load(path, 0); ((IShellLinkW)link).SetIconLocation(icon, 0); ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Save(path, true); SHChangeNotify(0x2000, 0x5, path, IntPtr.Zero); }
            finally { if (link != null) Marshal.FinalReleaseComObject(link); }
        }
        internal static void Autostart(bool enabled)
        {
            Autostart(enabled, StartupPath);
        }
        internal static void Autostart(bool enabled, string path)
        {
            if (enabled) Write(path, Executable, "--tray", L10n.T("NUMA Control — наблюдение за играми"));
            else if (File.Exists(path)) File.Delete(path);
        }
        internal static string Desktop(GameProfile game, string desktop)
        {
            string safe = string.Concat(game.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
            if (safe.Length > 65) safe = safe.Substring(0, 65);
            if (safe.Length == 0) safe = L10n.T("Игра");
            string path = Path.Combine(desktop, safe + " — NUMA.lnk");
            if (File.Exists(path) && !PointsToProfile(path, game.Id)) path = Path.Combine(desktop, safe + " — NUMA " + game.Id.Substring(0, 8) + ".lnk");
            string candidate = path;
            for (int number = 2; File.Exists(path) && !PointsToProfile(path, game.Id); number++) path = Path.Combine(desktop, Path.GetFileNameWithoutExtension(candidate) + " (" + number + ").lnk");
            Write(path, Executable, "--launch " + game.Id, L10n.T("Запустить ") + game.Name + L10n.T(" с профилем NUMA Control"));
            return path;
        }
        private static bool PointsToProfile(string path, string id)
        {
            object link = null;
            try
            {
                link = new ShellLinkObject();
                ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Load(path, 0);
                StringBuilder target = new StringBuilder(32768), arguments = new StringBuilder(512);
                ((IShellLinkW)link).GetPath(target, target.Capacity, IntPtr.Zero, 0);
                ((IShellLinkW)link).GetArguments(arguments, arguments.Capacity);
                return string.Equals(Path.GetFileName(target.ToString()), Path.GetFileName(Executable), StringComparison.OrdinalIgnoreCase) && arguments.ToString() == "--launch " + id;
            }
            catch (COMException) { return false; }
            finally { if (link != null) Marshal.FinalReleaseComObject(link); }
        }
    }

    internal sealed class LegacyKit
    {
        internal string Name, Prefix, ProcessName, ShortcutName, ExeName;
        internal string ShortcutOverride { get; set; }
        internal string Identity { get { return @"Local\" + Prefix + "-" + WindowsIdentity.GetCurrent().User.Value + "-" + ProcessName; } }
        internal string ShortcutPath { get { return ShortcutOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), ShortcutName + ".lnk"); } }
        internal bool Running
        {
            get
            {
                try { using (EventWaitHandle ready = EventWaitHandle.OpenExisting(Identity + "-Ready")) return ready.WaitOne(0); }
                catch (WaitHandleCannotBeOpenedException) { return false; }
            }
        }
        internal static List<LegacyKit> All()
        {
            return new List<LegacyKit> {
                new LegacyKit { Name = "CS2", Prefix = "CS2Affinity", ProcessName = "cs2", ShortcutName = "CS2 CPU Affinity", ExeName = "CS2-Affinity.exe" },
                new LegacyKit { Name = "Dota 2", Prefix = "Dota2Affinity", ProcessName = "dota2", ShortcutName = "Dota2 CPU Affinity", ExeName = "Dota2-Affinity.exe" },
                new LegacyKit { Name = "Warface", Prefix = "WarfaceAffinity", ProcessName = "game", ShortcutName = "Warface CPU Affinity", ExeName = "Warface-Affinity.exe" }
            };
        }
        internal void Retire(string backupDirectory)
        {
            if (File.Exists(ShortcutPath))
            {
                if (!string.Equals(Path.GetFileName(ShellLinks.Target(ShortcutPath)), ExeName, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(L10n.T("Ярлык «") + ShortcutName + L10n.T("» имеет неизвестную цель; используйте Disable-Autostart.cmd его комплекта."));
                Directory.CreateDirectory(backupDirectory);
                File.Move(ShortcutPath, Path.Combine(backupDirectory, ShortcutName + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".lnk"));
            }
            try { using (EventWaitHandle stop = EventWaitHandle.OpenExisting(Identity + "-Stop")) stop.Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
        }
    }

    internal static class GameLauncher
    {
        internal static bool Running(GameProfile game)
        {
            int session;
            using (Process self = Process.GetCurrentProcess()) session = self.SessionId;
            Process[] processes = Process.GetProcessesByName(game.ProcessName);
            try
            {
                foreach (Process p in processes)
                {
                    try
                    {
                        if (p.SessionId != session) continue;
                        using (SafeProcessHandle h = Native.Open(p.Id, false)) if (game.Matches(Native.Image(h))) return true;
                    }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }
                return false;
            }
            finally { foreach (Process p in processes) p.Dispose(); }
        }
        internal static void Launch(GameProfile game, ulong systemMask = 0, Func<ProcessStartInfo, Process> start = null)
        {
            if (start == null) start = Process.Start;
            if (Running(game)) return;
            string target = string.IsNullOrWhiteSpace(game.LaunchTarget) ? game.ExePath : game.LaunchTarget;
            if (string.IsNullOrWhiteSpace(target)) throw new InvalidOperationException(L10n.T("Укажите EXE или ссылку лаунчера в параметрах игры. Наблюдатель уже включён: игру можно запустить обычным способом."));
            Configuration.ValidateLaunch(target);
            if (LauncherTargets.IsClientUri(target) || game.LaunchViaShell)
            {
                var info = new ProcessStartInfo(target) { UseShellExecute = true };
                if (!Configuration.IsGameUri(target))
                {
                    if (!File.Exists(target)) throw new FileNotFoundException(L10n.T("Файл запуска не найден: ") + target);
                    Configuration.ValidateLaunchArguments(game.Arguments);
                    LauncherTargets.ValidateDirectory(game.LaunchDirectory);
                    info.Arguments = game.Arguments ?? "";
                    info.WorkingDirectory = string.IsNullOrEmpty(game.LaunchDirectory) ? Path.GetDirectoryName(target) : game.LaunchDirectory;
                }
                using (Process p = start(info)) { }
                return;
            }
            if (Configuration.IsGameUri(target))
            {
                using (Process p = start(new ProcessStartInfo(target) { UseShellExecute = true }))
                {
                    if (p != null && systemMask != 0) try { using (var handle = Native.Open(p.Id, true)) Native.Apply(handle, systemMask); } catch (System.ComponentModel.Win32Exception) { if (!p.HasExited) throw; }
                }
            }
            else
            {
                if (!File.Exists(target)) throw new FileNotFoundException(L10n.T("Файл запуска не найден: ") + target);
                using (Process p = start(new ProcessStartInfo(target, game.Arguments ?? "") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target) }))
                {
                    if (p != null && systemMask != 0)
                    {
                        try { using (var handle = Native.Open(p.Id, true)) Native.Apply(handle, game.Matches(target) ? game.CpuMask : systemMask); }
                        catch (System.ComponentModel.Win32Exception) { if (!p.HasExited) throw; }
                    }
                }
            }
        }
    }

    internal sealed class InstanceChannel : IDisposable
    {
        private readonly Mutex mutex;
        private readonly string pipeName;
        internal string PipeName { get { return pipeName; } }
        private readonly Thread listener;
        private volatile bool stopping;
        private readonly Action<string> receive;
        internal readonly bool Primary;
        private readonly object gate = new object();
        private NamedPipeServerStream server;
        internal InstanceChannel(string suffix, Action<string> handler)
        {
            string sid = WindowsIdentity.GetCurrent().User.Value;
            int session;
            using (Process self = Process.GetCurrentProcess()) session = self.SessionId;
            string name = "NumaControl-" + sid + "-" + session + suffix;
            mutex = new Mutex(true, @"Local\" + name, out Primary);
            pipeName = name;
            receive = handler;
            listener = new Thread(Listen) { IsBackground = true, Name = "Local commands" };
        }
        internal void Start() { if (Primary) listener.Start(); }
        internal void Send(string command)
        {
            using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                pipe.Connect(5000);
                byte[] bytes = Encoding.UTF8.GetBytes(command + "\n");
                pipe.Write(bytes, 0, bytes.Length);
                pipe.Flush();
                byte[] ack = new byte[1];
                IAsyncResult pending = pipe.BeginRead(ack, 0, 1, null, null);
                using (WaitHandle wait = pending.AsyncWaitHandle)
                {
                    if (!wait.WaitOne(5000) || pipe.EndRead(pending) != 1 || ack[0] != 1) throw new IOException(L10n.T("Приложение не подтвердило получение команды."));
                }
            }
        }
        private void Listen()
        {
            while (!stopping)
            {
                try
                {
                    PipeSecurity security = new PipeSecurity();
                    security.SetAccessRuleProtection(true, false);
                    security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
                    using (NamedPipeServerStream pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 512, 512, security))
                    {
                        lock (gate) { if (stopping) return; server = pipe; }
                        pipe.WaitForConnection();
                        byte[] bytes = new byte[128];
                        // Read through the terminator: an asynchronous byte pipe may split one command across reads.
                        int count = 0;
                        Stopwatch deadline = Stopwatch.StartNew();
                        while (count < bytes.Length && Array.IndexOf(bytes, (byte)'\n', 0, count) < 0)
                        {
                            IAsyncResult pending = pipe.BeginRead(bytes, count, bytes.Length - count, null, null);
                            using (WaitHandle wait = pending.AsyncWaitHandle)
                            {
                                if (!wait.WaitOne(Math.Max(0, 2000 - (int)deadline.ElapsedMilliseconds))) break;
                                int read = pipe.EndRead(pending);
                                if (read == 0) break;
                                count += read;
                            }
                        }
                        int end = Array.IndexOf(bytes, (byte)'\n', 0, count);
                        if (end < 0) continue;
                        string command = Encoding.UTF8.GetString(bytes, 0, end).Trim();
                        if (command == "show" || command.StartsWith("launch ", StringComparison.Ordinal)) receive(command);
                        pipe.WriteByte(1); pipe.Flush();
                    }
                }
                catch (IOException) { if (!stopping) Thread.Sleep(100); }
                catch (ObjectDisposedException) { if (stopping) return; }
                finally { lock (gate) server = null; }
            }
        }
        public void Dispose()
        {
            stopping = true;
            lock (gate) if (server != null) server.Dispose();
            if (listener.IsAlive) listener.Join(3000);
            if (Primary) mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }
}
