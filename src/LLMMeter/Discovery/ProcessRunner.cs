using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace LLMMeter.Discovery;

/// <summary>Runs an external process and captures stdout/stderr with a timeout.</summary>
public static class ProcessRunner
{
    public sealed record Result(int ExitCode, string StdOut, string StdErr);

    public static async Task<Result?> RunAsync(
        string fileName, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        try
        {
            using var proc = new Process { StartInfo = psi };
            if (!proc.Start()) return null;

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout);

            // Read raw stdout bytes: wsl.exe emits UTF-16LE, others UTF-8.
            var stdoutTask = ReadAllBytes(proc.StandardOutput.BaseStream, linked.Token);
            var stderrTask = ReadAllBytes(proc.StandardError.BaseStream, linked.Token);

            try
            {
                await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                var stdout = DecodeAuto(await stdoutTask.ConfigureAwait(false));
                var stderr = DecodeAuto(await stderrTask.ConfigureAwait(false));
                return new Result(proc.ExitCode, stdout, stderr);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return null;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or ObjectDisposedException)
        {
            return null; // binary missing / cannot start
        }
    }

    private static async Task<byte[]> ReadAllBytes(Stream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        try { await stream.CopyToAsync(ms, 81920, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw; // our timeout — propagate to caller's handler
        }
        catch
        {
            // stream ended early; keep what we have
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Auto-detects encoding: wsl.exe writes UTF-16LE even with --quiet,
    /// other tools write UTF-8. Sniffs null-byte density.
    /// </summary>
    public static string DecodeAuto(byte[] bytes)
    {
        if (bytes.Length == 0) return string.Empty;

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        int zeros = 0;
        int sample = Math.Min(bytes.Length, 512);
        for (int i = 1; i < sample; i += 2)
            if (bytes[i] == 0) zeros++;
        bool utf16 = sample > 4 && zeros > sample / 4;

        var s = utf16 ? Encoding.Unicode.GetString(bytes) : Encoding.UTF8.GetString(bytes);
        return s.Replace("\0", "");
    }

    /// <summary>Split output into non-empty trimmed lines.</summary>
    public static List<string> Lines(string text)
    {
        var list = new List<string>();
        foreach (var l in text.Split(['\r', '\n']))
        {
            var t = l.Trim().Trim('\0');
            if (t.Length > 0) list.Add(t);
        }
        return list;
    }
}
