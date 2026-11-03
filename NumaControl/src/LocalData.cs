using System;
using System.IO;
using System.Linq;
using System.Text;

namespace NumaControl
{
    internal static class LocalData
    {
        internal static string Resolve()
        {
            return Prepare(AppDomain.CurrentDomain.BaseDirectory, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NumaControl"));
        }
        internal static string Prepare(string applicationDirectory, string previous)
        {
            string portable = Path.Combine(applicationDirectory, "data");
            try
            {
                Directory.CreateDirectory(portable);
                string probe = Path.Combine(portable, ".write-" + Guid.NewGuid().ToString("N"));
                using (FileStream stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) stream.WriteByte(0);
                if (!File.Exists(Path.Combine(portable, "profiles.json")) && File.Exists(Path.Combine(previous, "profiles.json")))
                {
                    foreach (string name in new[] { "profiles.json", "profiles.json.bak" })
                        if (File.Exists(Path.Combine(previous, name))) File.Copy(Path.Combine(previous, name), Path.Combine(portable, name), false);
                    string previousReports = Path.Combine(previous, "bug-reports");
                    if (Directory.Exists(previousReports))
                    {
                        string reports = Path.Combine(portable, "bug-reports"); Directory.CreateDirectory(reports);
                        foreach (FileInfo report in new DirectoryInfo(previousReports).GetFiles("*.json").Where(f => f.Length < 2 * 1024 * 1024).OrderByDescending(f => f.LastWriteTimeUtc).Take(21))
                            File.Copy(report.FullName, Path.Combine(reports, report.Name), false);
                    }
                }
                return portable;
            }
            catch (UnauthorizedAccessException) { return previous; }
            catch (IOException) { return previous; }
        }
    }
    internal static class CpuInfoStore
    {
        internal static bool Save(string directory, Topology cpu)
        {
            string identity = cpu.Name + "|" + cpu.Count + "|" + cpu.Groups + "|" + string.Join(";", cpu.Cores.Select(c => c.Group + ":" + c.Mask.ToString("X") + ":" + c.Efficiency));
            string fingerprint;
            using (var sha = System.Security.Cryptography.SHA256.Create()) fingerprint = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(identity))).Replace("-", "");
            string path = Path.Combine(directory, "processor.json"); bool changed = false;
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length < 1048576)
                {
                    var previous = ProfileStore.Serializer().DeserializeObject(File.ReadAllText(path)) as System.Collections.Generic.Dictionary<string, object>;
                    if (previous != null && previous.ContainsKey("Fingerprint") && previous["Fingerprint"] is string && System.Text.RegularExpressions.Regex.IsMatch((string)previous["Fingerprint"], "^[0-9A-F]{64}$"))
                    {
                        if (string.Equals(previous["Fingerprint"] as string, fingerprint, StringComparison.Ordinal)) return false;
                        changed = true;
                    }
                }
            }
            catch (ArgumentException) { } catch (InvalidOperationException) { } catch (IOException) { }
            Directory.CreateDirectory(directory);
            ProfileStore.WriteAtomic(path, ProfileStore.Serializer().Serialize(new {
                Schema = 1, Fingerprint = fingerprint, DetectedUtc = DateTime.UtcNow.ToString("o"), Name = cpu.Name,
                LogicalProcessors = cpu.Count, PhysicalCores = cpu.Cores.Count, ProcessorGroups = cpu.Groups,
                Cores = cpu.Cores.Select(c => new { c.Index, c.Group, Mask = c.Mask.ToString("X"), EfficiencyClass = c.Efficiency }).ToArray(),
                Note = "Windows topology is queried at every start. This saved snapshot is never used instead of live hardware detection."
            }));
            return changed;
        }
    }
}
