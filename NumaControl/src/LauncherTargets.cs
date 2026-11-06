using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace NumaControl
{
    internal sealed class LauncherTarget
    {
        internal string Target;
        internal string Arguments;
        internal string Directory;
    }

    internal static class LauncherTargets
    {
        private const string EpicPart = @"[A-Za-z0-9][A-Za-z0-9_.-]*";
        private static readonly Regex ClientUri = new Regex(
            @"\A(?:com\.epicgames\.launcher://apps/(?:" + EpicPart + "%3A" + EpicPart + "%3A)?" + EpicPart +
            @"\?(?:action=launch(?:&silent=(?:true|false))?|silent=(?:true|false)&action=launch)|battlenet://[A-Za-z][A-Za-z0-9_-]*/?|uplay://launch/[0-9]+(?:/[0-9]+)?)\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal static bool IsClientUri(string target)
        {
            return target != null && target.Length <= 1024 && ClientUri.IsMatch(target);
        }

        internal static void ValidateDirectory(string directory)
        {
            if (string.IsNullOrEmpty(directory)) return;
            if (directory.Length > 1024 || !Regex.IsMatch(directory, @"\A[A-Za-z]:\\") ||
                directory.IndexOf('"') >= 0 || Path.GetFullPath(directory) != directory)
                throw new InvalidDataException(L10n.T("Укажите полный локальный путь к рабочей папке."));
        }

        internal static LauncherTarget ReadShortcut(string path)
        {
            ValidateDirectory(Path.GetDirectoryName(path));
            var file = new FileInfo(path);
            if (file.Length > 65536) throw InvalidShortcut();
            if (file.Extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
            {
                string target = null;
                bool internetShortcut = false;
                foreach (string line in File.ReadLines(path))
                {
                    string entry = line.Trim();
                    if (entry.StartsWith("[", StringComparison.Ordinal))
                        internetShortcut = entry.Equals("[InternetShortcut]", StringComparison.OrdinalIgnoreCase);
                    if (!internetShortcut || !entry.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) continue;
                    if (target != null) throw InvalidShortcut();
                    target = entry.Substring(4);
                }
                if (!Configuration.IsGameUri(target)) throw InvalidShortcut();
                return new LauncherTarget { Target = target, Arguments = "", Directory = "" };
            }

            if (!file.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)) throw InvalidShortcut();
            object link = new ShellLinkObject();
            try
            {
                ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Load(path, 0);
                var shortcut = (IShellLinkW)link;
                var target = new StringBuilder(32768);
                var arguments = new StringBuilder(32768);
                var directory = new StringBuilder(32768);
                shortcut.GetPath(target, target.Capacity, IntPtr.Zero, 4); // SLGP_RAWPATH, without resolving or running the link.
                shortcut.GetArguments(arguments, arguments.Capacity);
                shortcut.GetWorkingDirectory(directory, directory.Capacity);
                string executable = Environment.ExpandEnvironmentVariables(target.ToString());
                string workingDirectory = Environment.ExpandEnvironmentVariables(directory.ToString());
                if (executable.Length > 1024 || !Path.IsPathRooted(executable)) throw InvalidShortcut();
                Configuration.ValidateExe(executable);
                ValidateDirectory(Path.GetDirectoryName(executable));
                ValidateDirectory(workingDirectory);
                Configuration.ValidateLaunchArguments(arguments.ToString());
                if (string.Equals(executable, ShellLinks.Executable, StringComparison.OrdinalIgnoreCase)) throw InvalidShortcut();
                return new LauncherTarget { Target = executable, Arguments = arguments.ToString(), Directory = workingDirectory };
            }
            finally { Marshal.FinalReleaseComObject(link); }
        }

        private static InvalidDataException InvalidShortcut()
        {
            return new InvalidDataException(L10n.T("Выберите ярлык игры с путём к EXE или поддерживаемой ссылкой лаунчера."));
        }
    }
}
