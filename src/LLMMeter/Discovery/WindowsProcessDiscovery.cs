using System.Runtime.InteropServices;
using System.Text;

namespace LLMMeter.Discovery;

public sealed record TcpListenerInfo(string Address, int Port, int Pid);

/// <summary>
/// Enumerates listening TCP endpoints with owning PIDs via IpHlpApi
/// (GetExtendedTcpTable). Works without administrator rights.
/// Process-name heuristics are a discovery *optimization* only.
/// </summary>
public static class WindowsProcessDiscovery
{
    private const int AF_INET = 2;
    private const int AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_LISTENER = 3; // v4
    private const int TCP6_TABLE_OWNER_PID_LISTENER = 23; // v6

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddr = new byte[16];
        public uint LocalScopeId;
        public uint LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] RemoteAddr = new byte[16];
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningPid;

        public MibTcp6RowOwnerPid()
        {
            LocalAddr = new byte[16];
            RemoteAddr = new byte[16];
        }
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int dwSize, bool bOrder, int ulAf, int tableClass, int reserved);

    private const uint ListenState = 2; // MIB_TCP_STATE_LISTEN

    /// <summary>All LISTEN sockets on 127.0.0.1 / ::1 (plus wildcard) with PIDs.</summary>
    public static List<TcpListenerInfo> GetLoopbackListeners()
    {
        var list = new List<TcpListenerInfo>();
        CollectV4(list);
        CollectV6(list);
        return list;
    }

    private static void CollectV4(List<TcpListenerInfo> into)
    {
        int size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
        if (size <= 0) return;

        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buf, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0)
                return;

            int count = Marshal.ReadInt32(buf);
            IntPtr rowPtr = buf + sizeof(int);
            int rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();

            for (int i = 0; i < count && i < 4096; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPtr);
                rowPtr += rowSize;
                if (row.State != ListenState) continue;

                ushort port = SwapPort(row.LocalPort);
                string addr = FormatV4(row.LocalAddr);

                // Network scope: loopback and wildcard only — never probe LAN listeners.
                if (addr is "127.0.0.1" or "0.0.0.0")
                    into.Add(new TcpListenerInfo(addr, port, (int)row.OwningPid));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static void CollectV6(List<TcpListenerInfo> into)
    {
        int size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET6, TCP6_TABLE_OWNER_PID_LISTENER, 0);
        if (size <= 0) return;

        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buf, ref size, true, AF_INET6, TCP6_TABLE_OWNER_PID_LISTENER, 0) != 0)
                return;

            int count = Marshal.ReadInt32(buf);
            IntPtr rowPtr = buf + sizeof(int);
            int rowSize = Marshal.SizeOf<MibTcp6RowOwnerPid>();

            for (int i = 0; i < count && i < 4096; i++)
            {
                var row = Marshal.PtrToStructure<MibTcp6RowOwnerPid>(rowPtr);
                rowPtr += rowSize;
                if (row.State != ListenState) continue;

                ushort port = SwapPort(row.LocalPort);
                bool isWildcard = row.LocalAddr.All(b => b == 0);
                bool isLoopback = row.LocalAddr[..15].All(b => b == 0) && row.LocalAddr[15] == 1;

                if (isLoopback)
                    into.Add(new TcpListenerInfo("[::1]", port, (int)row.OwningPid));
                else if (isWildcard)
                    into.Add(new TcpListenerInfo("[::]", port, (int)row.OwningPid));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    internal static ushort SwapPort(uint networkOrderPort)
    {
        // Port arrives in the high bytes of the DWORD? No: low word, big-endian.
        uint v = networkOrderPort & 0xFFFF;
        return (ushort)(((v & 0xFF00) >> 8) | ((v & 0x00FF) << 8));
    }

    internal static string FormatV4(uint addr)
    {
        // Network byte order: little-endian octets on Windows.
        return $"{addr & 0xFF}.{(addr >> 8) & 0xFF}.{(addr >> 16) & 0xFF}.{(addr >> 24) & 0xFF}";
    }

    /// <summary>
    /// Heuristic process-name match. Returns true when the process plausibly
    /// hosts an inference server; used only to prioritize probing.
    /// </summary>
    public static bool IsLikelyInferenceProcess(string processName)
    {
        var n = processName.ToLowerInvariant();
        return n switch
        {
            "llama-server" or "llama-cli" or "llama-server.exe" => true,
            "ollama" or "ollama_llama_server" or "ollama app" => true,
            "lm studio" or "lm-studio" or "lms" => true,
            "ninfer" or "ninfer-serve" or "ninfer.exe" or "ninfer-serve.exe" => true,
            "python" or "python3" or "pythonw" or "py" or "vllm" => true, // confirm via cmdline/HTTP
            _ => false,
        };
    }

}
