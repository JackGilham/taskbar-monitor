using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace TaskbarMonitor.Counters
{
    /// <summary>
    /// CPU temperature read from LibreHardwareMonitor's local web server
    /// (LibreHardwareMonitor: Options > Remote Web Server > Run).
    ///
    /// Windows has no user-mode API for the real CPU temperature; reading it needs a kernel driver and
    /// admin rights. Rather than running taskbar-monitor elevated, we let an already-running
    /// LibreHardwareMonitor do that and only read its data.json here.
    /// </summary>
    class CounterCPUTemp : ICounter
    {
        public const string CounterName = "CPU TEMP";

        static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);
        static readonly TimeSpan WaitForFreshReading = TimeSpan.FromMilliseconds(300);
        static readonly TimeSpan RetryWhenUnreachable = TimeSpan.FromSeconds(5);
        static readonly TimeSpan ReadingExpiresAfter = TimeSpan.FromSeconds(10);

        // Names LibreHardwareMonitor gives the headline CPU temperature, best first.
        // AMD Zen: "Core (Tctl/Tdie)" (Zen 2+), "Core (Tdie)" (Zen 1 with Tctl offset). Intel: "CPU Package".
        static readonly string[] PreferredSensorNames =
        {
            "Core (Tctl/Tdie)", "Core (Tdie)", "CPU Package", "Core Max", "CCDs Max (Tdie)", "Core (Tctl)", "Core Average"
        };

        static readonly Regex CpuTemperatureSensorId = new Regex(@"^/(amdcpu|intelcpu|genericcpu)/\d+/temperature/", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex Number = new Regex(@"-?\d+(?:[.,]\d+)?", RegexOptions.Compiled);

        static readonly HttpClient Http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = RequestTimeout };

        readonly object sync = new object();
        Task fetch;
        DateTime nextAttemptUtc = DateTime.MinValue;
        float? lastCelsius;
        DateTime lastReadingUtc = DateTime.MinValue;

        public CounterCPUTemp(Options options)
            : base(options)
        {
        }

        internal override void Initialize(PerformanceCounterReader reader)
        {
            lock (ThreadLock)
            {
                InfoSummary = TemperatureInfo.Create("summary");
                Infos = new List<CounterInfo> { TemperatureInfo.Create("CPU") };
            }
        }

        public override void Update()
        {
            Task pending = StartFetch();
            if (pending != null)
                pending.Wait(WaitForFreshReading); // localhost round trip is normally a few ms

            float? celsius;
            lock (sync)
            {
                celsius = DateTime.UtcNow - lastReadingUtc < ReadingExpiresAfter ? lastCelsius : null;
            }

            lock (ThreadLock)
            {
                TemperatureInfo.Push(InfoSummary, celsius, Options.HistorySize);
                TemperatureInfo.Push(Infos[0], celsius, Options.HistorySize);
            }
        }

        Task StartFetch()
        {
            lock (sync)
            {
                if (fetch != null && !fetch.IsCompleted)
                    return fetch;
                if (DateTime.UtcNow < nextAttemptUtc)
                    return null;

                string url = string.IsNullOrWhiteSpace(Options.LibreHardwareMonitorUrl)
                    ? TaskbarMonitor.Options.DefaultLibreHardwareMonitorUrl
                    : Options.LibreHardwareMonitorUrl;
                fetch = FetchAsync(url);
                return fetch;
            }
        }

        async Task FetchAsync(string url)
        {
            try
            {
                string json = await Http.GetStringAsync(url).ConfigureAwait(false);
                float celsius;
                if (TryReadCpuTemperature(json, out celsius))
                {
                    lock (sync)
                    {
                        lastCelsius = celsius;
                        lastReadingUtc = DateTime.UtcNow;
                    }
                }
            }
            catch (Exception)
            {
                // LibreHardwareMonitor not running, web server off, or unexpected response: back off and retry
                lock (sync)
                {
                    nextAttemptUtc = DateTime.UtcNow + RetryWhenUnreachable;
                }
            }
        }

        /// <summary>Finds the CPU temperature in LibreHardwareMonitor's data.json.</summary>
        internal static bool TryReadCpuTemperature(string json, out float celsius)
        {
            celsius = 0;
            var readings = new List<KeyValuePair<string, float>>(); // sensor name, °C

            var pending = new Stack<JToken>();
            pending.Push(JToken.Parse(json));
            while (pending.Count > 0)
            {
                var node = pending.Pop() as JObject;
                if (node == null)
                    continue;

                string sensorId = (string)node["SensorId"];
                if ((string)node["Type"] == "Temperature" && sensorId != null && CpuTemperatureSensorId.IsMatch(sensorId))
                {
                    string name = (string)node["Text"] ?? "";
                    float value;
                    // RawValue is always °C; Value follows the unit chosen in LibreHardwareMonitor
                    if (!name.Contains("Distance") && TryParseCelsius((string)node["RawValue"] ?? (string)node["Value"], out value))
                        readings.Add(new KeyValuePair<string, float>(name, value));
                }

                var children = node["Children"] as JArray;
                if (children != null)
                {
                    foreach (var child in children)
                        pending.Push(child);
                }
            }

            if (readings.Count == 0)
                return false;

            foreach (string preferred in PreferredSensorNames)
            {
                var matches = readings.Where(x => x.Key == preferred).ToList();
                if (matches.Count > 0)
                {
                    celsius = matches.Max(x => x.Value); // hottest socket on multi-CPU systems
                    return true;
                }
            }

            celsius = readings.Max(x => x.Value);
            return true;
        }

        static bool TryParseCelsius(string text, out float celsius)
        {
            celsius = 0;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            Match match = Number.Match(text);
            if (!match.Success || !float.TryParse(match.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out celsius))
                return false;

            if (text.Contains("°F"))
                celsius = (celsius - 32) / 1.8f;
            return true;
        }

        public override string GetName()
        {
            return CounterName;
        }

        public override CounterType GetCounterType()
        {
            return CounterType.SINGLE;
        }
    }
}
