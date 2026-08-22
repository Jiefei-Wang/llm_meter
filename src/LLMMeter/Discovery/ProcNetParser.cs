using System.Globalization;

namespace LLMMeter.Discovery;

public sealed record ProcNetListener(string AddressHex, int Port, bool LoopbackOrWildcard);

/// <summary>
/// Parses Linux /proc/net/tcp and /proc/net/tcp6 content for LISTEN sockets.
/// Pure text parsing — unit tested without WSL.
/// </summary>
public static class ProcNetParser
{
    public static List<ProcNetListener> ParseListeners(string content)
    {
        var result = new List<ProcNetListener>();
        if (string.IsNullOrEmpty(content)) return result;

        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || char.IsDigit(line[0]) == false) continue; // skip header

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) continue;

            // sl, local_address, rem_address, st, ...
            var local = parts[1];
            string state = parts[3];
            if (state != "0A") continue; // 0A = TCP_LISTEN

            int colon = local.LastIndexOf(':');
            if (colon <= 0) continue;
            string addrHex = local[..colon];
            if (!int.TryParse(local[(colon + 1)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int port))
                continue;

            bool loopbackOrWildcard = IsLoopbackOrWildcard(addrHex);
            result.Add(new ProcNetListener(addrHex, port, loopbackOrWildcard));
        }
        return result;
    }

    /// <summary>IPv4 hex is little-endian per byte-group; IPv6 is 32 hex chars.</summary>
    internal static bool IsLoopbackOrWildcard(string addrHex)
    {
        if (addrHex.Length == 8)
        {
            return addrHex is "0100007F"   // 127.0.0.1
                or "00000000";            // 0.0.0.0
        }
        if (addrHex.Length == 32)
        {
            if (addrHex.All(c => c == '0')) return true; // ::
            // ::1 in kernel order = 00000000 00000000 00000000 01000000
            return addrHex.Equals("00000000000000000000000001000000", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }
}
