using System.Net.Sockets;
using System.Text;

namespace AvaSnap.Services;

/// <summary>
/// Minimal OSC-over-UDP receiver for the two VRChat user-camera parameters that
/// replace image-based detection of "is the camera UI open" and "orientation":
/// <c>/usercamera/Mode</c> (0 = closed, non-zero = some camera mode active) and
/// <c>/usercamera/OrientationIsLandscape</c>. VRChat sends these out whenever the
/// state changes, so this only needs to listen -- no query/handshake required.
/// Hand-rolled parser instead of a NuGet dependency: OSC-over-UDP messages are a
/// simple binary format (null-padded address string, type-tag string, then
/// 4-byte-aligned args), and VRChat only ever sends simple messages or bundles of
/// them, so a full OSC library would be a lot of unused surface.
/// </summary>
public sealed class VrChatOscListener : IDisposable
{
    /// <summary>VRChat's default outgoing OSC port (configurable in-game, but this
    /// covers the vast majority of installs).</summary>
    public const int DefaultPort = 9001;

    public event Action<bool>? CameraModeChanged;
    public event Action<bool>? OrientationChanged;

    public bool? IsCameraOpen { get; private set; }

    /// <summary>Last known orientation from VRChat's own OSC output.</summary>
    public bool? IsLandscape { get; private set; }

    private UdpClient? _client;
    private CancellationTokenSource? _cts;

    public void Start(int port = DefaultPort)
    {
        Stop();
        try
        {
            _client = new UdpClient(port);
        }
        catch (SocketException)
        {
            // Port already in use (e.g. another OSC listener running) -- give up
            // quietly. Callers just keep whatever behavior they use when OSC
            // state stays unknown.
            _client = null;
            return;
        }

        _cts = new CancellationTokenSource();
        _ = ListenLoopAsync(_client, _cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        _client?.Dispose();
        _client = null;
    }

    private async Task ListenLoopAsync(UdpClient client, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await client.ReceiveAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch
            {
                continue; // transient recv error; keep listening
            }

            try
            {
                HandlePacket(result.Buffer);
            }
            catch
            {
                // malformed/unexpected packet; ignore and keep listening
            }
        }
    }

    private void HandlePacket(byte[] data)
    {
        if (data.Length >= 16 && data.Length >= 7 && Encoding.ASCII.GetString(data, 0, 7) == "#bundle")
        {
            int offset = 16; // "#bundle\0" (8 bytes) + 8-byte timetag
            while (offset + 4 <= data.Length)
            {
                int size = ReadInt32(data, offset);
                offset += 4;
                if (size <= 0 || offset + size > data.Length) break;
                HandlePacket(data.AsSpan(offset, size).ToArray());
                offset += size;
            }
            return;
        }

        var (address, afterAddress) = ReadOscString(data, 0);
        if (afterAddress >= data.Length) return;
        var (typeTags, afterTags) = ReadOscString(data, afterAddress);
        if (typeTags.Length < 2 || typeTags[0] != ',') return;

        object? arg = ReadArg(data, afterTags, typeTags[1]);

        switch (address)
        {
            case "/usercamera/Mode":
                if (ToInt(arg) is { } mode)
                {
                    IsCameraOpen = mode != 0;
                    CameraModeChanged?.Invoke(IsCameraOpen.Value);
                }
                break;
            case "/usercamera/OrientationIsLandscape":
                if (ToBool(arg) is { } landscape)
                {
                    IsLandscape = landscape;
                    OrientationChanged?.Invoke(landscape);
                }
                break;
        }
    }

    private static object? ReadArg(byte[] data, int offset, char tag) => tag switch
    {
        'i' => offset + 4 <= data.Length ? ReadInt32(data, offset) : null,
        'f' => offset + 4 <= data.Length ? ReadFloat32(data, offset) : null,
        'T' => true,
        'F' => false,
        _ => null,
    };

    private static int? ToInt(object? arg) => arg switch
    {
        int i => i,
        float f => (int)Math.Round(f),
        bool b => b ? 1 : 0,
        _ => null,
    };

    private static bool? ToBool(object? arg) => arg switch
    {
        bool b => b,
        int i => i != 0,
        float f => f != 0,
        _ => null,
    };

    private static (string Value, int NextOffset) ReadOscString(byte[] data, int offset)
    {
        int end = offset;
        while (end < data.Length && data[end] != 0) end++;
        string s = Encoding.ASCII.GetString(data, offset, end - offset);
        int len = end - offset + 1; // include null terminator
        int padded = (len + 3) / 4 * 4; // pad to 4-byte boundary
        return (s, offset + padded);
    }

    private static int ReadInt32(byte[] data, int offset) =>
        (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];

    private static float ReadFloat32(byte[] data, int offset)
    {
        Span<byte> bytes = stackalloc byte[4];
        data.AsSpan(offset, 4).CopyTo(bytes);
        if (BitConverter.IsLittleEndian) bytes.Reverse();
        return BitConverter.ToSingle(bytes);
    }

    public void Dispose() => Stop();
}
