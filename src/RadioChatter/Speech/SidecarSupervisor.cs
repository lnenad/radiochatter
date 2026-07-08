using BepInEx;
using BepInEx.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading.Tasks;

namespace RadioChatter.Speech
{
    internal sealed class SidecarSupervisor
    {
        public enum SidecarStatus
        {
            Unknown,
            Starting,
            Available,
            Unavailable
        }

        private readonly Config _config;
        private readonly ManualLogSource _log;
        private readonly object _gate = new object();
        private SidecarStatus _status = SidecarStatus.Unknown;
        private DateTime _nextProbeUtc = DateTime.MinValue;
        private DateTime _nextLaunchUtc = DateTime.MinValue;
        private DateTime _nextWarningLogUtc = DateTime.MinValue;
        private bool _probeRunning;
        private bool _disposed;
        private int _consecutiveFailures;
        private Process _process;

        public SidecarSupervisor(Config config, ManualLogSource log)
        {
            _config = config;
            _log = log;
        }

        public SidecarStatus Status
        {
            get
            {
                lock (_gate)
                    return _status;
            }
        }

        public bool CanRequestAudio
        {
            get
            {
                lock (_gate)
                    return !_disposed && (_status == SidecarStatus.Unknown || _status == SidecarStatus.Available);
            }
        }

        public void Tick()
        {
            DateTime now = DateTime.UtcNow;
            lock (_gate)
            {
                if (_disposed || _probeRunning || now < _nextProbeUtc)
                    return;

                _probeRunning = true;
            }

            Task.Run((Action)ProbeSidecar);
        }

        public void ReportRequestSuccess()
        {
            MarkAvailable();
        }

        public void ReportRequestFailure(string message)
        {
            DateTime now = DateTime.UtcNow;
            int failures;
            bool shouldLog;

            lock (_gate)
            {
                if (_disposed)
                    return;

                _status = SidecarStatus.Unavailable;
                _consecutiveFailures++;
                failures = _consecutiveFailures;
                _nextProbeUtc = now.AddSeconds(2);
                shouldLog = now >= _nextWarningLogUtc;
                if (shouldLog)
                    _nextWarningLogUtc = now.AddSeconds(15);
            }

            if (shouldLog)
                _log?.LogWarning($"Pocket TTS request failed ({failures} consecutive): {message}. Continuing subtitles-only; probing /health.");
        }

        public void Shutdown()
        {
            Process process;
            lock (_gate)
            {
                _disposed = true;
                process = _process;
                _process = null;
            }

            if (process == null)
                return;

            if (_config.StopSidecarOnExit.Value)
                StopProcessTree(process);

            process.Dispose();
        }

        private void StopProcessTree(Process process)
        {
            try
            {
                if (process.HasExited)
                    return;

                if (IsUnixLike())
                {
                    // run_sidecar.sh execs the python server, so the launched process is
                    // the server itself.
                    process.Kill();
                }
                else
                {
                    // On Windows the launched process is a cmd.exe wrapper around python;
                    // only a tree kill takes the server down with it.
                    using (Process killer = Process.Start(new ProcessStartInfo
                    {
                        FileName = "taskkill",
                        Arguments = $"/PID {process.Id} /T /F",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }))
                    {
                        killer?.WaitForExit(3000);
                    }
                }

                _log?.LogInfo("Stopped Pocket TTS sidecar with the game.");
            }
            catch (Exception ex)
            {
                _log?.LogWarning($"Failed to stop Pocket TTS sidecar: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void ProbeSidecar()
        {
            try
            {
                string error;
                if (CheckHealth(out error))
                {
                    MarkAvailable();
                    return;
                }

                string launchError;
                bool launched = TryLaunchSidecar(out launchError);
                MarkUnavailable(error, launched, launchError);
            }
            finally
            {
                lock (_gate)
                    _probeRunning = false;
            }
        }

        private bool CheckHealth(out string error)
        {
            try
            {
                string url = _config.SidecarUrl.Value.TrimEnd('/') + "/health";
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.Accept = "application/json";
                request.Timeout = 1000;
                request.ReadWriteTimeout = 1000;

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        error = null;
                        return true;
                    }

                    error = $"HTTP {(int)response.StatusCode}";
                    return false;
                }
            }
            catch (WebException ex)
            {
                HttpWebResponse response = ex.Response as HttpWebResponse;
                error = response != null ? $"HTTP {(int)response.StatusCode}" : ex.Message;
                return false;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        private bool TryLaunchSidecar(out string error)
        {
            error = null;
            if (!_config.AutoStartSidecar.Value)
                return false;

            DateTime now = DateTime.UtcNow;
            lock (_gate)
            {
                if (_disposed)
                    return false;

                // A previously launched sidecar that is still alive is most likely loading the
                // model; report "launched" so we keep waiting instead of spawning another copy.
                if (IsProcessRunning(_process))
                    return true;

                if (now < _nextLaunchUtc)
                    return false;

                _nextLaunchUtc = now.AddSeconds(60);
            }

            string command = ResolveCommand(out error);
            if (string.IsNullOrEmpty(command))
                return false;

            try
            {
                ProcessStartInfo info = CreateStartInfo(command);
                Process process = Process.Start(info);
                lock (_gate)
                {
                    if (!_disposed)
                        _process = process;
                    else
                        process?.Dispose();
                }

                _log?.LogInfo($"Starting Pocket TTS sidecar: {command}");
                return true;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        private void MarkAvailable()
        {
            bool shouldLog;
            DateTime now = DateTime.UtcNow;
            lock (_gate)
            {
                if (_disposed)
                    return;

                shouldLog = _status != SidecarStatus.Available;
                _status = SidecarStatus.Available;
                _consecutiveFailures = 0;
                _nextProbeUtc = now.AddSeconds(30);
            }

            if (shouldLog)
                _log?.LogInfo("Pocket TTS sidecar is available; voice audio enabled.");
        }

        private void MarkUnavailable(string healthError, bool launched, string launchError)
        {
            bool shouldLog;
            DateTime now = DateTime.UtcNow;
            lock (_gate)
            {
                if (_disposed)
                    return;

                _status = launched ? SidecarStatus.Starting : SidecarStatus.Unavailable;
                _nextProbeUtc = now.AddSeconds(launched ? 2 : 5);
                shouldLog = now >= _nextWarningLogUtc;
                if (shouldLog)
                    _nextWarningLogUtc = now.AddSeconds(30);
            }

            if (!shouldLog)
                return;

            if (launched)
            {
                _log?.LogInfo("Pocket TTS sidecar health check failed; waiting for auto-started sidecar to come online.");
                return;
            }

            if (!_config.AutoStartSidecar.Value)
            {
                _log?.LogWarning($"Pocket TTS sidecar unavailable ({healthError}). Continuing subtitles-only. Start the sidecar manually or enable AutoStartSidecar.");
                return;
            }

            if (!string.IsNullOrEmpty(launchError))
                _log?.LogWarning($"Pocket TTS sidecar unavailable ({healthError}). Auto-start failed: {launchError}. Continuing subtitles-only.");
            else
                _log?.LogWarning($"Pocket TTS sidecar unavailable ({healthError}). Continuing subtitles-only; auto-start retry is cooling down.");
        }

        private string ResolveCommand(out string error)
        {
            error = null;
            string configured = TrimCommand(_config.SidecarCommand.Value);
            if (!string.IsNullOrEmpty(configured))
            {
                string expanded = ExpandPath(configured);
                string candidate = Path.IsPathRooted(expanded) ? expanded : Path.Combine(Paths.PluginPath, expanded);
                if (File.Exists(candidate))
                    return candidate;

                error = $"SidecarCommand does not exist: {candidate}";
                return null;
            }

            string[] launcherNames = IsUnixLike()
                ? new[] { "run_sidecar.sh", "run_sidecar.bat" }
                : new[] { "run_sidecar.bat", "run_sidecar.sh" };
            string[] directories =
            {
                Path.Combine(Paths.PluginPath, "RadioChatter", "sidecar"),
                Path.Combine(Paths.PluginPath, "RadioChatter"),
                Path.Combine(Paths.PluginPath, "sidecar"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sidecar")
            };

            for (int i = 0; i < directories.Length; i++)
            {
                for (int j = 0; j < launcherNames.Length; j++)
                {
                    string candidate = Path.Combine(directories[i], launcherNames[j]);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }

            error = "No default sidecar launcher found next to the plugin; set SidecarCommand.";
            return null;
        }

        private static bool IsProcessRunning(Process process)
        {
            if (process == null)
                return false;

            try { return !process.HasExited; }
            catch { return false; }
        }

        private static ProcessStartInfo CreateStartInfo(string command)
        {
            string extension = Path.GetExtension(command).ToLowerInvariant();
            ProcessStartInfo info;
            if (extension == ".bat" || extension == ".cmd")
            {
                info = new ProcessStartInfo
                {
                    FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                    Arguments = "/c " + QuoteArgument(command)
                };
            }
            else if (extension == ".sh" && IsUnixLike())
            {
                info = new ProcessStartInfo
                {
                    FileName = "/bin/sh",
                    Arguments = QuoteArgument(command)
                };
            }
            else
            {
                info = new ProcessStartInfo
                {
                    FileName = command
                };
            }

            info.WorkingDirectory = Path.GetDirectoryName(command) ?? AppDomain.CurrentDomain.BaseDirectory;
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.WindowStyle = ProcessWindowStyle.Hidden;
            return info;
        }

        private static string TrimCommand(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            value = value.Trim();
            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
                return value.Substring(1, value.Length - 2);

            return value;
        }

        private static string ExpandPath(string value)
        {
            value = Environment.ExpandEnvironmentVariables(value);
            if (value == "~")
                return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal))
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), value.Substring(2));
            return value;
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static bool IsUnixLike()
        {
            PlatformID platform = Environment.OSVersion.Platform;
            return platform == PlatformID.Unix || platform == PlatformID.MacOSX;
        }
    }
}
