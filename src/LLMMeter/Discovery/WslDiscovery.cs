using System.Diagnostics;

namespace LLMMeter.Discovery;

public sealed record WslDistroInfo(string Name, IReadOnlyList<int> ListeningPorts, string? IpAddress);

/// <summary>
/// Enumerates *running* WSL distributions and their listening TCP ports.
/// Never starts a stopped distro. Reads /proc/net/tcp{,6} via cat — no ss
/// dependency inside the guest.
/// </summary>
public static class WslDiscovery
{
    private const string WslExe = "wsl.exe";
    public static readonly TimeSpan DistroListTimeout = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan PerDistroTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Returns names of running distros, or empty when WSL is absent.</summary>
    public static async Task<List<string>> GetRunningDistrosAsync(CancellationToken ct)
    {
        var res = await ProcessRunner.RunAsync(WslExe, "--list --running --quiet", DistroListTimeout, ct).ConfigureAwait(false);
        if (res is null || res.ExitCode != 0) return [];

        var names = new List<string>();
        foreach (var line in ProcessRunner.Lines(res.StdOut))
        {
            // Some builds append "(Default)" markers on non-quiet output; quiet should be clean.
            var name = line.Trim();
            if (name.Length > 0 && !name.StartsWith("Windows Subsystem", StringComparison.OrdinalIgnoreCase))
                names.Add(name);
        }
        return names;
    }

    /// <summary>Listening ports inside one distro (loopback or wildcard).</summary>
    public static async Task<List<int>> GetListeningPortsAsync(string distro, CancellationToken ct)
    {
        var res = await ProcessRunner.RunAsync(
            WslExe, $"-d \"{distro}\" sh -c \"cat /proc/net/tcp /proc/net/tcp6 2>/dev/null\"",
            PerDistroTimeout, ct).ConfigureAwait(false);

        if (res is null) return [];

        var ports = new HashSet<int>();
        foreach (var listener in ProcNetParser.ParseListeners(res.StdOut))
        {
            if (!listener.LoopbackOrWildcard) continue;
            if (listener.Port is < 1 or > 65535) continue;
            ports.Add(listener.Port);
        }
        return [.. ports];
    }

    /// <summary>First IPv4 address of the distro (fallback target for NAT mode).</summary>
    public static async Task<string?> GetDistroIpAsync(string distro, CancellationToken ct)
    {
        var res = await ProcessRunner.RunAsync(
            WslExe, $"-d \"{distro}\" hostname -I", PerDistroTimeout, ct).ConfigureAwait(false);
        if (res is null) return null;

        var first = res.StdOut.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(ip => ip.Split('.').Length == 4);
        return first;
    }

    /// <summary>True when wsl.exe exists at all.</summary>
    public static bool IsWslInstalled()
    {
        try
        {
            var psi = new ProcessStartInfo("wsl.exe", "--status")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            return p != null;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<List<WslDistroInfo>> ScanAsync(CancellationToken ct)
    {
        if (!IsWslInstalled()) return [];

        var distros = await GetRunningDistrosAsync(ct).ConfigureAwait(false);
        using var gate = new SemaphoreSlim(4, 4);
        var tasks = distros.Select(async d =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var ports = await GetListeningPortsAsync(d, ct).ConfigureAwait(false);
                string? ip = ports.Count > 0
                    ? await GetDistroIpAsync(d, ct).ConfigureAwait(false)
                    : null;
                return new WslDistroInfo(d, ports, ip);
            }
            finally { gate.Release(); }
        });
        return [.. await Task.WhenAll(tasks).ConfigureAwait(false)];
    }
}
