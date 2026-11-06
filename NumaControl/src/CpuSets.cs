using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NumaControl
{
    internal static class CpuSets
    {
        internal const uint SetLimited = 0x2000;
        private const int InsufficientBuffer = 122;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemCpuSetInformation(IntPtr buffer, uint length, out uint required, SafeProcessHandle process, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessDefaultCpuSets(SafeProcessHandle process, [Out] uint[] ids, uint count, out uint required);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessDefaultCpuSets(SafeProcessHandle process, uint[] ids, uint count);

        internal static uint[] Select(SafeProcessHandle process, ulong mask)
        {
            if (mask == 0 || (mask & ~Native.ReadMask(process)) != 0) throw Unavailable();
            uint size;
            if (!GetSystemCpuSetInformation(IntPtr.Zero, 0, out size, process, 0) &&
                Marshal.GetLastWin32Error() != InsufficientBuffer)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            IntPtr buffer = Marshal.AllocHGlobal(checked((int)size));
            try
            {
                if (!GetSystemCpuSetInformation(buffer, size, out size, process, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                var ids = new List<uint>();
                ulong found = 0;
                for (int offset = 0; offset < size;)
                {
                    // SYSTEM_CPU_SET_INFORMATION begins with Size and Type. Read the CpuSet union only for type zero.
                    if (size - offset < 8) throw Unavailable();
                    IntPtr entry = IntPtr.Add(buffer, offset);
                    int length = Marshal.ReadInt32(entry);
                    if (length < 8 || length > size - offset) throw Unavailable();
                    if (length >= 20 && Marshal.ReadInt32(entry, 4) == 0 && Marshal.ReadInt16(entry, 12) == 0)
                    {
                        int cpu = Marshal.ReadByte(entry, 14);
                        int flags = Marshal.ReadByte(entry, 19);
                        bool reservedForAnotherProcess = (flags & 0x02) != 0 && (flags & 0x04) == 0;
                        if (cpu < 64 && (mask & (1UL << cpu)) != 0 && !reservedForAnotherProcess)
                        {
                            found |= 1UL << cpu;
                            ids.Add(unchecked((uint)Marshal.ReadInt32(entry, 8)));
                        }
                    }
                    offset += length;
                }
                if (found != mask) throw Unavailable();
                ids.Sort();
                return ids.ToArray();
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        internal static bool Matches(SafeProcessHandle process, uint[] expected)
        {
            uint count;
            if (GetProcessDefaultCpuSets(process, null, 0, out count)) return expected.Length == 0;
            if (Marshal.GetLastWin32Error() != InsufficientBuffer)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (count != expected.Length) return false;
            uint[] actual = new uint[count];
            if (!GetProcessDefaultCpuSets(process, actual, count, out count))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            Array.Sort(actual);
            return count == expected.Length && actual.SequenceEqual(expected);
        }

        internal static void Apply(SafeProcessHandle process, uint[] ids)
        {
            if (!SetProcessDefaultCpuSets(process, ids.Length == 0 ? null : ids, (uint)ids.Length))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!Matches(process, ids))
                throw new InvalidOperationException(L10n.T("Windows не подтвердила выбранный набор CPU Sets."));
        }

        private static InvalidOperationException Unavailable()
        {
            return new InvalidOperationException(L10n.T("Часть выбранных ЦП недоступна для назначения через CPU Sets."));
        }
    }
}
