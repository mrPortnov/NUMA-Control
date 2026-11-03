using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NumaControl
{
    internal sealed class GameIcons
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
        private readonly string directory;
        private readonly Dictionary<string, ImageSource> memory = new Dictionary<string, ImageSource>();
        internal GameIcons(string dataDirectory) { directory = Path.Combine(dataDirectory, "icons"); }
        internal ImageSource Get(GameProfile game)
        {
            string key = game.Id + "|" + game.ExePath + "|" + game.LaunchTarget; ImageSource cached;
            if (memory.TryGetValue(key, out cached)) return cached;
            string executable = Source(game);
            ImageSource result = null;
            try
            {
                string path = FileName(executable);
                if (File.Exists(path) && new FileInfo(path).Length < 1048576)
                    try { result = Load(path); } catch (Exception error) { if (!(error is IOException || error is FileFormatException || error is NotSupportedException || error is System.Runtime.InteropServices.COMException || error is ArgumentException)) throw; File.Delete(path); }
                if (result == null && !string.IsNullOrWhiteSpace(executable) && File.Exists(executable))
                {
                    IntPtr large, small;
                    ExtractIconEx(executable, 0, out large, out small, 1);
                    try
                    {
                        IntPtr icon = large != IntPtr.Zero ? large : small;
                        if (icon != IntPtr.Zero)
                        {
                            BitmapSource bitmap = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(64, 64));
                            bitmap.Freeze(); Directory.CreateDirectory(directory);
                            PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                            using (MemoryStream bytes = new MemoryStream()) { encoder.Save(bytes); WriteImage(path, bytes.ToArray()); }
                            result = bitmap;
                        }
                    }
                    finally { if (large != IntPtr.Zero) DestroyIcon(large); if (small != IntPtr.Zero && small != large) DestroyIcon(small); }
                }
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { } catch (ArgumentException) { } catch (NotSupportedException) { } catch (System.Runtime.InteropServices.COMException) { }
            // Bound the cache across profile edits. Missing icons are cached too; no polling/extraction in the watcher.
            if (memory.Count >= 200) memory.Clear(); memory[key] = result; return result;
        }
        private static string Source(GameProfile game)
        {
            if (!string.IsNullOrWhiteSpace(game.ExePath)) return game.ExePath;
            if (!Configuration.IsGameUri(game.LaunchTarget)) return game.LaunchTarget;
            if (!game.LaunchTarget.StartsWith("steam://rungameid/", StringComparison.OrdinalIgnoreCase)) return "";
            string appId = game.LaunchTarget.Substring("steam://rungameid/".Length);
            foreach (Microsoft.Win32.RegistryView view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
                try
                {
                    using (var root = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, view))
                    using (var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App " + appId))
                    {
                        string icon = key == null ? null : key.GetValue("DisplayIcon") as string;
                        if (string.IsNullOrWhiteSpace(icon)) continue;
                        icon = icon.Trim();
                        if (icon.StartsWith("\"", StringComparison.Ordinal)) { int end = icon.IndexOf('"', 1); if (end > 0) icon = icon.Substring(1, end - 1); }
                        else { int suffix = icon.LastIndexOf(','); int index; if (suffix > 0 && int.TryParse(icon.Substring(suffix + 1), out index)) icon = icon.Substring(0, suffix); }
                        if (File.Exists(icon)) return icon;
                    }
                }
                catch (System.Security.SecurityException) { } catch (UnauthorizedAccessException) { }
            return "";
        }
        private string FileName(string source)
        {
            string identity = source == null ? "" : source.ToUpperInvariant();
            if (File.Exists(source)) { FileInfo info = new FileInfo(source); identity += "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks; }
            using (var hash = System.Security.Cryptography.SHA256.Create()) return Path.Combine(directory, BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(identity))).Replace("-", "") + ".png");
        }
        private static BitmapImage Load(string path)
        {
            BitmapImage image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(path); image.DecodePixelWidth = 64; image.EndInit(); image.Freeze(); return image;
        }
        private static void WriteImage(string path, byte[] bytes)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(temporary, bytes); if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
