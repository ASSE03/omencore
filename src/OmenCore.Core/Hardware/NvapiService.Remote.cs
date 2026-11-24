using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace OmenCore.Hardware
{
    /// <summary>
    /// Out-of-process NVAPI.
    ///
    /// On an RTX 3070 Laptop OMEN 15-ek1xxx (BIOS 15.42) Windows bugchecked 0x9F/3 (DXG power IRP
    /// timeout) about ten minutes after the display went off whenever nvapi64.dll had been loaded
    /// into the OmenCore process, even with every NVAPI call stood down and NvAPI_Unload called
    /// (the module stays mapped). It did not happen with NVAPI never loaded
    /// (OMENCORE_DISABLE_NVAPI), nor after opening NVAPI and then exiting OmenCore: process exit is
    /// what releases the driver. So the real NVAPI work runs in a child process
    /// (<c>OmenCore.exe --nvapi-host</c>), and the away quiet mode ends that process instead of
    /// unloading in place. Implemented, pending confirmation on that machine.
    ///
    /// The app opts in by setting <see cref="HostExecutablePath"/> at startup. Left null (tests,
    /// other frontends, OMENCORE_NVAPI_INPROC=1) the service runs in-process exactly as before.
    ///
    /// Protocol: one JSON object per line. Request {"op":"...","arg":n}; response
    /// {"ok":true,"result":...,"state":{...}} where state carries every public property, so the
    /// proxy's getters stay current after each call. One call at a time.
    /// </summary>
    public partial class NvapiService
    {
        public const string HostArgument = "--nvapi-host";

        /// <summary>Executable started with <see cref="HostArgument"/>; null runs NVAPI in-process.</summary>
        public static string? HostExecutablePath { get; set; }

        private static readonly JsonSerializerOptions HostJson = new() { IncludeFields = true };

        private NvapiHostClient? _remote;

        private bool UseRemote =>
            HostExecutablePath != null &&
            !string.Equals(Environment.GetEnvironmentVariable("OMENCORE_NVAPI_INPROC"), "1", StringComparison.Ordinal);

        private sealed class StateSnapshot
        {
            public bool Initialized { get; set; }
            public bool SupportsOverclocking { get; set; }
            public int GpuCount { get; set; }
            public int CoreClockOffsetMHz { get; set; }
            public int MemoryClockOffsetMHz { get; set; }
            public int VoltageOffsetMv { get; set; }
            public int PowerLimitPercent { get; set; }
            public bool SupportsPowerLimit { get; set; }
            public string PowerLimitStatusMessage { get; set; } = "";
            public int MinCoreOffset { get; set; }
            public int MaxCoreOffset { get; set; }
            public int MinMemoryOffset { get; set; }
            public int MaxMemoryOffset { get; set; }
            public int MinPowerLimit { get; set; }
            public int MaxPowerLimit { get; set; }
            public int DefaultPowerLimitWatts { get; set; }
            public string GpuName { get; set; } = "Unknown";
        }

        private StateSnapshot CaptureState() => new()
        {
            Initialized = _initialized,
            SupportsOverclocking = SupportsOverclocking,
            GpuCount = GpuCount,
            CoreClockOffsetMHz = CoreClockOffsetMHz,
            MemoryClockOffsetMHz = MemoryClockOffsetMHz,
            VoltageOffsetMv = VoltageOffsetMv,
            PowerLimitPercent = PowerLimitPercent,
            SupportsPowerLimit = SupportsPowerLimit,
            PowerLimitStatusMessage = PowerLimitStatusMessage,
            MinCoreOffset = MinCoreOffset,
            MaxCoreOffset = MaxCoreOffset,
            MinMemoryOffset = MinMemoryOffset,
            MaxMemoryOffset = MaxMemoryOffset,
            MinPowerLimit = MinPowerLimit,
            MaxPowerLimit = MaxPowerLimit,
            DefaultPowerLimitWatts = DefaultPowerLimitWatts,
            GpuName = GpuName
        };

        private void ApplyState(StateSnapshot s)
        {
            _initialized = s.Initialized;
            SupportsOverclocking = s.SupportsOverclocking;
            GpuCount = s.GpuCount;
            CoreClockOffsetMHz = s.CoreClockOffsetMHz;
            MemoryClockOffsetMHz = s.MemoryClockOffsetMHz;
            VoltageOffsetMv = s.VoltageOffsetMv;
            PowerLimitPercent = s.PowerLimitPercent;
            SupportsPowerLimit = s.SupportsPowerLimit;
            PowerLimitStatusMessage = s.PowerLimitStatusMessage;
            MinCoreOffset = s.MinCoreOffset;
            MaxCoreOffset = s.MaxCoreOffset;
            MinMemoryOffset = s.MinMemoryOffset;
            MaxMemoryOffset = s.MaxMemoryOffset;
            MinPowerLimit = s.MinPowerLimit;
            MaxPowerLimit = s.MaxPowerLimit;
            DefaultPowerLimitWatts = s.DefaultPowerLimitWatts;
            GpuName = s.GpuName;
        }

        /// <summary>
        /// Runs a remote call when the host is in use; returns false (caller runs in-process) otherwise.
        /// A host that died or timed out leaves the service unavailable, like a failed init.
        /// </summary>
        private bool TryRemote<T>(string op, int? arg, T fallback, out T result)
        {
            result = fallback;
            var remote = _remote;
            if (remote == null)
            {
                return false;
            }

            if (!remote.TryCall(op, arg, out var response))
            {
                _logging.Warn($"NVAPI host: {op} failed ({remote.LastError}); NVAPI unavailable until the next start");
                StopRemote();
                _initialized = false;
                return true;
            }

            if (response.TryGetProperty("state", out var state))
            {
                var snapshot = state.Deserialize<StateSnapshot>(HostJson);
                if (snapshot != null) ApplyState(snapshot);
            }

            if (response.TryGetProperty("result", out var value) && value.ValueKind != JsonValueKind.Null)
            {
                try
                {
                    result = value.Deserialize<T>(HostJson)!;
                }
                catch (JsonException ex)
                {
                    _logging.Warn($"NVAPI host: unreadable {op} result: {ex.Message}");
                }
            }

            return true;
        }

        private bool InitializeRemote()
        {
            StopRemote();
            var client = NvapiHostClient.Start(HostExecutablePath!, _logging);
            if (client == null)
            {
                _logging.Warn("NVAPI host: could not start; NVAPI unavailable");
                return false;
            }

            _remote = client;
            TryRemote("Initialize", null, false, out bool ok);
            if (!ok)
            {
                // Nothing to keep the process for: no NVIDIA GPU, or the driver refused.
                StopRemote();
            }

            return ok;
        }

        private void StopRemote()
        {
            var remote = Interlocked.Exchange(ref _remote, null);
            remote?.Dispose();
        }

        /// <summary>
        /// Body of <c>OmenCore.exe --nvapi-host</c>: owns the real NVAPI session and serves the
        /// parent until its stdin closes (parent exit or the away mode ending the host).
        /// </summary>
        public static int RunHost(Services.LoggingService logging, TextReader input, TextWriter output)
        {
            HostExecutablePath = null; // this process is the in-process side
            var service = new NvapiService(logging);

            string? line;
            while ((line = input.ReadLine()) != null)
            {
                object? result = null;
                bool ok = true;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var op = doc.RootElement.GetProperty("op").GetString();
                    int arg = doc.RootElement.TryGetProperty("arg", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt32() : 0;
                    result = op switch
                    {
                        "Initialize" => service.Initialize(),
                        "SetCoreClockOffset" => service.SetCoreClockOffset(arg),
                        "SetMemoryClockOffset" => service.SetMemoryClockOffset(arg),
                        "SetVoltageOffset" => service.SetVoltageOffset(arg),
                        "SetPowerLimit" => service.SetPowerLimit(arg),
                        "GetGpuLoad" => service.GetGpuLoad(),
                        "GetGpuTemperature" => service.GetGpuTemperature(),
                        "GetGpuVramUsage" => service.GetGpuVramUsage(),
                        "GetGpuPowerWatts" => service.GetGpuPowerWatts(),
                        "GetLoadAndVramOnly" => service.GetLoadAndVramOnly(),
                        "GetMonitoringSample" => service.GetMonitoringSample(),
                        "GetCurrentClocks" => service.GetCurrentClocks(),
                        "ResetToDefaults" => service.ResetToDefaults(),
                        _ => throw new InvalidOperationException($"unknown op {op}")
                    };
                }
                catch (Exception ex)
                {
                    ok = false;
                    logging.Warn($"NVAPI host: request failed: {ex.Message}");
                }

                output.WriteLine(JsonSerializer.Serialize(new { ok, result, state = service.CaptureState() }, HostJson));
                output.Flush();
            }

            service.Dispose();
            return 0;
        }

        private sealed class NvapiHostClient : IDisposable
        {
            private readonly Process _process;
            private readonly object _gate = new();
            private bool _dead;

            public string LastError { get; private set; } = "";

            private NvapiHostClient(Process process) => _process = process;

            public static NvapiHostClient? Start(string exe, Services.LoggingService logging)
            {
                try
                {
                    var psi = new ProcessStartInfo(exe, HostArgument)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = System.Text.Encoding.UTF8,
                        StandardErrorEncoding = System.Text.Encoding.UTF8
                    };
                    var process = Process.Start(psi);
                    if (process == null) return null;

                    // The host forwards its own log lines on stderr.
                    process.ErrorDataReceived += (_, e) =>
                    {
                        if (!string.IsNullOrWhiteSpace(e.Data)) logging.Info($"[NVAPI host] {e.Data}");
                    };
                    process.BeginErrorReadLine();
                    logging.Info($"NVAPI host: started (PID {process.Id})");
                    return new NvapiHostClient(process);
                }
                catch (Exception ex)
                {
                    logging.Warn($"NVAPI host: start failed: {ex.Message}");
                    return null;
                }
            }

            public bool TryCall(string op, int? arg, out JsonElement response)
            {
                response = default;
                lock (_gate)
                {
                    if (_dead || _process.HasExited)
                    {
                        LastError = "host not running";
                        return false;
                    }

                    try
                    {
                        var request = arg.HasValue
                            ? JsonSerializer.Serialize(new { op, arg = arg.Value })
                            : JsonSerializer.Serialize(new { op });
                        _process.StandardInput.WriteLine(request);
                        _process.StandardInput.Flush();

                        var readTask = _process.StandardOutput.ReadLineAsync();
                        var timeout = op == "Initialize" ? 20000 : 10000;
                        if (!readTask.Wait(timeout) || readTask.Result == null)
                        {
                            LastError = readTask.IsCompleted ? "host exited" : $"no answer within {timeout / 1000}s";
                            _dead = true;
                            return false;
                        }

                        using var doc = JsonDocument.Parse(readTask.Result);
                        response = doc.RootElement.Clone();
                        if (response.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
                        {
                            LastError = "host reported an error";
                        }
                        return true;
                    }
                    catch (Exception ex)
                    {
                        LastError = ex.Message;
                        _dead = true;
                        return false;
                    }
                }
            }

            public void Dispose()
            {
                lock (_gate)
                {
                    _dead = true;
                }

                try
                {
                    // Closing stdin lets the host dispose NVAPI and exit on its own; whatever is
                    // still running after that is ended. Either way the process goes, and with it
                    // every driver handle it held.
                    _process.StandardInput.Close();
                    if (!_process.WaitForExit(3000))
                    {
                        _process.Kill(entireProcessTree: true);
                        _process.WaitForExit(3000);
                    }
                }
                catch (Exception)
                {
                    // Already gone.
                }
                finally
                {
                    _process.Dispose();
                }
            }
        }
    }
}
