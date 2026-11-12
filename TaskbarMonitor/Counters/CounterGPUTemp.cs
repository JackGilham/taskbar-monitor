using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace TaskbarMonitor.Counters
{
    /// <summary>
    /// GPU temperature as reported by the display driver through the D3DKMT adapter performance data,
    /// the same source Task Manager uses for its "GPU Temperature". Needs no admin rights and no extra
    /// driver; works for dedicated GPUs whose driver supports WDDM 2.4+ (Windows 10 1803+).
    /// </summary>
    class CounterGPUTemp : ICounter
    {
        public const string CounterName = "GPU TEMP";

        readonly GpuTemperatureReader gpus = new GpuTemperatureReader();

        public CounterGPUTemp(Options options)
            : base(options)
        {
        }

        internal override void Initialize(PerformanceCounterReader reader)
        {
            lock (ThreadLock)
            {
                InfoSummary = TemperatureInfo.Create("summary");
                Infos = new List<CounterInfo> { TemperatureInfo.Create("GPU") };
            }
        }

        public override void Update()
        {
            float? celsius = gpus.ReadHottestCelsius();

            lock (ThreadLock)
            {
                TemperatureInfo.Push(InfoSummary, celsius, Options.HistorySize);
                TemperatureInfo.Push(Infos[0], celsius, Options.HistorySize);
            }
        }

        public override string GetName()
        {
            return CounterName;
        }

        public override CounterType GetCounterType()
        {
            return CounterType.SINGLE;
        }

        public new static bool IsAvailable()
        {
            using (var probe = new GpuTemperatureReader())
            {
                return probe.ReadHottestCelsius().HasValue;
            }
        }

        public override void Dispose()
        {
            gpus.Dispose();
        }
    }

    /// <summary>Shared value handling for the temperature counters (0-100 °C scale).</summary>
    static class TemperatureInfo
    {
        public const float MaximumCelsius = 100.0f;

        public static CounterInfo Create(string name)
        {
            return new CounterInfo() { Name = name, History = new List<float>(), MaximumValue = MaximumCelsius };
        }

        /// <param name="celsius">Latest reading, or null when no reading is available.</param>
        public static void Push(CounterInfo info, float? celsius, int historySize)
        {
            float graphValue = celsius.HasValue ? Math.Max(0, Math.Min(celsius.Value, info.MaximumValue)) : 0;
            info.CurrentValue = graphValue;
            info.History.Add(graphValue);
            while (info.History.Count > historySize)
                info.History.RemoveAt(0);

            info.CurrentStringValue = celsius.HasValue
                ? celsius.Value.ToString("0") + "°C"
                : "--°C";
        }
    }

    /// <summary>
    /// Reads GPU temperatures with D3DKMTQueryAdapterInfo(KMTQAITYPE_ADAPTERPERFDATA).
    /// Keeps the adapter handles open between reads and re-enumerates when an adapter goes away
    /// (driver update, TDR, eGPU unplugged).
    /// </summary>
    sealed class GpuTemperatureReader : IDisposable
    {
        static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);

        readonly object sync = new object();
        List<uint> adapters;
        DateTime nextEnumerationUtc = DateTime.MinValue;

        /// <summary>Temperature of the hottest GPU in °C, or null if no adapter reports one.</summary>
        public float? ReadHottestCelsius()
        {
            lock (sync)
            {
                try
                {
                    if (adapters == null)
                    {
                        if (DateTime.UtcNow < nextEnumerationUtc)
                            return null;

                        adapters = OpenAdaptersWithTemperature();
                        if (adapters.Count == 0)
                        {
                            adapters = null;
                            nextEnumerationUtc = DateTime.UtcNow + RetryInterval;
                            return null;
                        }
                    }

                    float? hottest = null;
                    foreach (uint adapter in adapters)
                    {
                        float? celsius = QueryCelsius(adapter);
                        if (!celsius.HasValue)
                        {
                            // adapter handle no longer valid: enumerate again on the next read
                            CloseAll();
                            return hottest;
                        }
                        if (celsius.Value > 0 && (!hottest.HasValue || celsius.Value > hottest.Value))
                            hottest = celsius;
                    }
                    return hottest;
                }
                catch (Exception ex) when (ex is EntryPointNotFoundException || ex is DllNotFoundException)
                {
                    // D3DKMT not available on this version of Windows
                    CloseAll();
                    nextEnumerationUtc = DateTime.MaxValue;
                    return null;
                }
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                CloseAll();
            }
        }

        void CloseAll()
        {
            if (adapters == null) return;
            foreach (uint adapter in adapters)
                Close(adapter);
            adapters = null;
        }

        static List<uint> OpenAdaptersWithTemperature()
        {
            var result = new List<uint>();

            // first call (no buffer) returns the adapter count
            var enumArgs = new D3DKMT_ENUMADAPTERS2();
            D3DKMTEnumAdapters2(ref enumArgs);
            if (enumArgs.NumAdapters == 0)
                return result;

            int infoSize = Marshal.SizeOf(typeof(D3DKMT_ADAPTERINFO));
            IntPtr buffer = Marshal.AllocHGlobal(infoSize * (int)enumArgs.NumAdapters);
            try
            {
                enumArgs.pAdapters = buffer;
                if (D3DKMTEnumAdapters2(ref enumArgs) != STATUS_SUCCESS)
                    return result;

                for (int i = 0; i < enumArgs.NumAdapters; i++)
                {
                    var info = (D3DKMT_ADAPTERINFO)Marshal.PtrToStructure(IntPtr.Add(buffer, i * infoSize), typeof(D3DKMT_ADAPTERINFO));
                    float? celsius = QueryCelsius(info.hAdapter);
                    if (celsius.HasValue && celsius.Value > 0)
                        result.Add(info.hAdapter);
                    else
                        Close(info.hAdapter); // e.g. Microsoft Basic Render Driver, or no sensor
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            return result;
        }

        /// <returns>°C, or null if the query failed.</returns>
        static float? QueryCelsius(uint adapter)
        {
            int size = Marshal.SizeOf(typeof(D3DKMT_ADAPTER_PERFDATA));
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(new D3DKMT_ADAPTER_PERFDATA { PhysicalAdapterIndex = 0 }, buffer, false);
                var query = new D3DKMT_QUERYADAPTERINFO
                {
                    hAdapter = adapter,
                    Type = KMTQAITYPE_ADAPTERPERFDATA,
                    pPrivateDriverData = buffer,
                    PrivateDriverDataSize = (uint)size
                };
                if (D3DKMTQueryAdapterInfo(ref query) != STATUS_SUCCESS)
                    return null;

                var perf = (D3DKMT_ADAPTER_PERFDATA)Marshal.PtrToStructure(buffer, typeof(D3DKMT_ADAPTER_PERFDATA));
                return perf.Temperature / 10.0f; // reported in deci-Celsius
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        static void Close(uint adapter)
        {
            var args = new D3DKMT_CLOSEADAPTER { hAdapter = adapter };
            D3DKMTCloseAdapter(ref args);
        }

        #region d3dkmthk.h
        const int STATUS_SUCCESS = 0;
        const int KMTQAITYPE_ADAPTERPERFDATA = 62;

        [StructLayout(LayoutKind.Sequential)]
        struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct D3DKMT_ADAPTERINFO
        {
            public uint hAdapter;
            public LUID AdapterLuid;
            public uint NumOfSources;
            public int bPrecisePresentRegionsPreferred;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct D3DKMT_ENUMADAPTERS2
        {
            public uint NumAdapters;
            public IntPtr pAdapters;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct D3DKMT_QUERYADAPTERINFO
        {
            public uint hAdapter;
            public int Type;
            public IntPtr pPrivateDriverData;
            public uint PrivateDriverDataSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct D3DKMT_ADAPTER_PERFDATA
        {
            public uint PhysicalAdapterIndex;
            public ulong MemoryFrequency;
            public ulong MaxMemoryFrequency;
            public ulong MaxMemoryFrequencyOC;
            public ulong MemoryBandwidth;
            public ulong PCIEBandwidth;
            public uint FanRPM;
            public uint Power;
            public uint Temperature;
            public byte PowerStateOverride;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct D3DKMT_CLOSEADAPTER
        {
            public uint hAdapter;
        }

        [DllImport("gdi32.dll")]
        static extern int D3DKMTEnumAdapters2(ref D3DKMT_ENUMADAPTERS2 args);

        [DllImport("gdi32.dll")]
        static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO args);

        [DllImport("gdi32.dll")]
        static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER args);
        #endregion
    }
}
