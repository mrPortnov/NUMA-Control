using System;
using System.IO;
using System.Runtime.InteropServices;

namespace NumaControl
{
    internal static class InstallMaintenance
    {
        internal static void Cleanup(string executable, string startupShortcut, string desktop)
        {
            DeleteOwnedShortcut(startupShortcut, executable);
            try
            {
                if (Directory.Exists(desktop))
                    foreach (string path in Directory.GetFiles(desktop, "* — NUMA*.lnk")) DeleteOwnedShortcut(path, executable);
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        private static void DeleteOwnedShortcut(string path, string executable)
        {
            try
            {
                string target = ShellLinks.Target(path);
                if (!string.IsNullOrEmpty(target) && string.Equals(Path.GetFullPath(target), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)) File.Delete(path);
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { } catch (COMException) { } catch (ArgumentException) { }
        }
    }
}
