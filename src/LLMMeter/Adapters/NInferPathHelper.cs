using System.IO;
using LLMMeter.Core;

namespace LLMMeter.Adapters;

/// <summary>
/// Deterministic telemetry path resolution for native NInfer servers.
/// Maps Linux paths into Windows-accessible UNC paths when running in WSL.
/// </summary>
public static class NInferPathHelper
{
    public static string BuildNInferLinuxTelemetryPath(int port) =>
        $"/tmp/llmmeter/ninfer-{port}.requests.jsonl";

    public static string BuildNInferWindowsTelemetryPath(int port) =>
        Path.Combine(Path.GetTempPath(), "LLMMeter", $"ninfer-{port}.requests.jsonl");

    public static string BuildNInferWslTelemetryPath(string distro, int port) =>
        $@"\\wsl.localhost\{distro}\tmp\llmmeter\ninfer-{port}.requests.jsonl";

    public static string BuildNInferWslFallbackTelemetryPath(string distro, int port) =>
        $@"\\wsl$\{distro}\tmp\llmmeter\ninfer-{port}.requests.jsonl";

    /// <summary>
    /// Returns the host file path where LLMMeter (running on Windows) can read NInfer's telemetry JSONL log.
    /// </summary>
    public static string ResolveHostTelemetryPath(EndpointRef endpoint)
    {
        int port = endpoint.BaseUrl.Port;

        if (endpoint.Origin == OriginKind.Wsl && !string.IsNullOrWhiteSpace(endpoint.WslDistro))
        {
            string modern = BuildNInferWslTelemetryPath(endpoint.WslDistro, port);
            if (File.Exists(modern)) return modern;

            string fallback = BuildNInferWslFallbackTelemetryPath(endpoint.WslDistro, port);
            if (File.Exists(fallback)) return fallback;

            return modern;
        }

        if (endpoint.Origin == OriginKind.WindowsHost)
        {
            return BuildNInferWindowsTelemetryPath(port);
        }

        // Manual or unspecified origin: check Windows location first, then probe WSL distros.
        string winPath = BuildNInferWindowsTelemetryPath(port);
        if (File.Exists(winPath)) return winPath;

        if (endpoint.BaseUrl.IsLoopback || endpoint.BaseUrl.Host is "127.0.0.1" or "localhost" or "::1")
        {
            try
            {
                string wslRoot = @"\\wsl.localhost";
                if (Directory.Exists(wslRoot))
                {
                    foreach (var dir in Directory.GetDirectories(wslRoot))
                    {
                        string distro = Path.GetFileName(dir);
                        string wslPath = BuildNInferWslTelemetryPath(distro, port);
                        if (File.Exists(wslPath)) return wslPath;
                    }
                }
            }
            catch { }

            try
            {
                string wslFallbackRoot = @"\\wsl$";
                if (Directory.Exists(wslFallbackRoot))
                {
                    foreach (var dir in Directory.GetDirectories(wslFallbackRoot))
                    {
                        string distro = Path.GetFileName(dir);
                        string wslPath = BuildNInferWslFallbackTelemetryPath(distro, port);
                        if (File.Exists(wslPath)) return wslPath;
                    }
                }
            }
            catch { }
        }

        return winPath;
    }
}
