using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskbarMonitor
{
    public static class MonitorIdentity
    {
        // This is the monitor's device interface, not the session's DISPLAY1/2 alias.
        // Moving a monitor to a different connector can change its interface identity.
        public static string GetId(string deviceName)
        {
            if (string.IsNullOrEmpty(deviceName))
            {
                return null;
            }

            HashSet<string> identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (uint index = 0; ; index++)
            {
                DisplayDevice device = new DisplayDevice { Size = Marshal.SizeOf(typeof(DisplayDevice)) };
                if (!EnumDisplayDevices(deviceName, index, ref device, 1))
                {
                    break;
                }
                if ((device.StateFlags & 1) != 0 && !string.IsNullOrWhiteSpace(device.DeviceId))
                {
                    identities.Add(device.DeviceId.ToUpperInvariant());
                }
            }

            // A cloned output can represent multiple monitors; do not guess which one was selected.
            if (identities.Count != 1)
            {
                return null;
            }
            foreach (string identity in identities)
            {
                return identity;
            }
            return null;
        }

        public static Dictionary<string, string> Capture()
        {
            Dictionary<string, string> identities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Screen screen in Screen.AllScreens)
            {
                identities.Add(screen.DeviceName, GetId(screen.DeviceName));
            }
            return identities;
        }

        public static bool SameDisplays(IDictionary<string, string> first, IDictionary<string, string> second)
        {
            if (first.Count != second.Count)
            {
                return false;
            }
            foreach (KeyValuePair<string, string> item in first)
            {
                string identity;
                if (!second.TryGetValue(item.Key, out identity) ||
                    !string.Equals(item.Value, identity, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return true;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DisplayDevice
        {
            public int Size;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplayDevices(string deviceName, uint index, ref DisplayDevice device, uint flags);
    }
}
