using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using ValheimOne.Infrastructure;

namespace ValheimOne.Query;

internal sealed class QueryResponder
{
    private const int AppId = 892970;
    private const int MaximumDatagramSize = 1400;
    private const int MaximumChallenges = 256;
    private const long ChallengeLifetimeTicks = TimeSpan.TicksPerSecond * 30;

    private static readonly byte EnvironmentByte =
        Environment.OSVersion.Platform == PlatformID.Win32NT ? (byte)'w' : (byte)'l';
    private static readonly byte[] RequestPrefix = { 0xff, 0xff, 0xff, 0xff };
    private static readonly byte[] InfoRequestName = Encoding.ASCII.GetBytes("Source Engine Query\0");

    private readonly int _port;
    private readonly Func<QuerySnapshot> _snapshotProvider;
    private readonly ModLogger _log;
    private readonly RandomNumberGenerator _random = RandomNumberGenerator.Create();
    private readonly Dictionary<IPEndPoint, ChallengeEntry> _challenges = new();
    private UdpClient? _socket;
    private Thread? _thread;
    private volatile bool _stopping;
    private bool _bindWarningLogged;
    private bool _workerWarningLogged;

    public QueryResponder(int port, Func<QuerySnapshot> snapshotProvider, ModLogger log)
    {
        _port = port;
        _snapshotProvider = snapshotProvider;
        _log = log;
    }

    public bool IsRunning => _thread != null && _thread.IsAlive;

    public bool Start()
    {
        if (IsRunning)
        {
            return true;
        }

        CloseSocket();
        UdpClient socket;
        try
        {
            socket = new UdpClient(new IPEndPoint(IPAddress.Any, _port));
        }
        catch (Exception exception)
        {
            if (!_bindWarningLogged)
            {
                _bindWarningLogged = true;
                _log.Warning(
                    $"[Query] A2S responder could not bind UDP {_port}: " +
                    $"{exception.GetType().Name}: {exception.Message}");
            }

            return false;
        }

        _stopping = false;
        _workerWarningLogged = false;
        _socket = socket;
        _thread = new Thread(() => Listen(socket))
        {
            IsBackground = true,
            Name = "ValheimOne.Query",
        };
        _thread.Start();
        _log.Info($"[Query] A2S responder listening on UDP {_port}");
        return true;
    }

    public void Stop()
    {
        bool wasRunning = IsRunning;
        _stopping = true;
        CloseSocket();

        Thread? thread = _thread;
        if (thread != null && thread.IsAlive && !ReferenceEquals(Thread.CurrentThread, thread))
        {
            thread.Join();
        }

        _thread = null;
        _challenges.Clear();
        _random.Dispose();
        if (wasRunning)
        {
            _log.Info($"[Query] A2S responder stopped on UDP {_port}");
        }
    }

    private void Listen(UdpClient socket)
    {
        while (!_stopping)
        {
            try
            {
                var remoteEndpoint = new IPEndPoint(IPAddress.Any, 0);
                byte[] datagram = socket.Receive(ref remoteEndpoint);
                HandleDatagram(socket, remoteEndpoint, datagram);
            }
            catch (ObjectDisposedException)
            {
                if (!_stopping)
                {
                    LogWorkerFailure("UDP socket was disposed unexpectedly.");
                }
            }
            catch (SocketException exception)
            {
                if (!_stopping)
                {
                    LogWorkerFailure($"SocketException: {exception.Message}");
                }
            }
            catch (Exception exception)
            {
                if (!_stopping)
                {
                    LogWorkerFailure($"{exception.GetType().Name}: {exception.Message}");
                }
            }
        }
    }

    private void HandleDatagram(UdpClient socket, IPEndPoint remoteEndpoint, byte[] datagram)
    {
        if (datagram.Length < 5 || datagram.Length > MaximumDatagramSize || !HasPrefix(datagram))
        {
            return;
        }

        switch (datagram[4])
        {
            case 0x54:
                HandleInfoRequest(socket, remoteEndpoint, datagram);
                break;
            case 0x55:
                HandlePlayerRequest(socket, remoteEndpoint, datagram);
                break;
        }
    }

    private void HandleInfoRequest(UdpClient socket, IPEndPoint remoteEndpoint, byte[] datagram)
    {
        int baseLength = 5 + InfoRequestName.Length;
        if ((datagram.Length != baseLength && datagram.Length != baseLength + 4) ||
            !MatchesInfoRequestName(datagram))
        {
            return;
        }

        if (datagram.Length == baseLength ||
            !HasValidChallenge(remoteEndpoint, ReadInt32(datagram, baseLength)))
        {
            SendChallenge(socket, remoteEndpoint);
            return;
        }

        QuerySnapshot snapshot = _snapshotProvider();
        using var writer = new PacketWriter();
        writer.WriteHeader(0x49);
        writer.WriteByte(17);
        writer.WriteString(snapshot.ServerName);
        writer.WriteString(snapshot.WorldName);
        writer.WriteString("valheim");
        writer.WriteString("Valheim");
        writer.WriteInt16(unchecked((short)AppId));
        writer.WriteByte(ClampByte(snapshot.PlayerCount));
        writer.WriteByte(ClampByte(snapshot.MaxPlayers));
        writer.WriteByte(0);
        writer.WriteByte((byte)'d');
        writer.WriteByte(EnvironmentByte);
        writer.WriteByte(snapshot.Passworded ? (byte)1 : (byte)0);
        writer.WriteByte(0);
        writer.WriteString(snapshot.GameVersion);
        writer.WriteByte(0x80 | 0x20 | 0x01);
        writer.WriteInt16(unchecked((short)snapshot.GamePort));
        writer.WriteString($"valheimone,vo={snapshot.PluginVersion}");
        writer.WriteUInt64(AppId);
        Send(socket, remoteEndpoint, writer.ToArray());
    }

    private void HandlePlayerRequest(UdpClient socket, IPEndPoint remoteEndpoint, byte[] datagram)
    {
        if (datagram.Length != 9)
        {
            return;
        }

        int challenge = ReadInt32(datagram, 5);
        if (challenge == -1 || !HasValidChallenge(remoteEndpoint, challenge))
        {
            SendChallenge(socket, remoteEndpoint);
            return;
        }

        QuerySnapshot snapshot = _snapshotProvider();
        int playerCount = Math.Min(255, Math.Max(0, snapshot.PlayerCount));
        double uptimeSeconds = (DateTime.UtcNow - snapshot.StartTimeUtc).TotalSeconds;
        float duration = uptimeSeconds <= 0d
            ? 0f
            : uptimeSeconds >= float.MaxValue
                ? float.MaxValue
                : (float)uptimeSeconds;

        using var writer = new PacketWriter();
        writer.WriteHeader(0x44);
        writer.WriteByte((byte)playerCount);
        for (int index = 0; index < playerCount; index++)
        {
            string playerName = snapshot.GetPlayerName(index);
            if (string.IsNullOrEmpty(playerName))
            {
                playerName = $"Player {index + 1}";
            }

            writer.WriteByte((byte)index);
            writer.WriteString(playerName);
            writer.WriteInt32(0);
            writer.WriteSingle(duration);
        }

        Send(socket, remoteEndpoint, writer.ToArray());
    }

    private bool HasValidChallenge(IPEndPoint remoteEndpoint, int challenge)
    {
        long nowTicks = DateTime.UtcNow.Ticks;
        RemoveExpiredChallenges(nowTicks);
        return _challenges.TryGetValue(remoteEndpoint, out ChallengeEntry? entry) &&
               entry.ExpiresTicks > nowTicks &&
               entry.Value == challenge;
    }

    private void SendChallenge(UdpClient socket, IPEndPoint remoteEndpoint)
    {
        int challenge = CreateChallenge(remoteEndpoint);
        using var writer = new PacketWriter();
        writer.WriteHeader(0x41);
        writer.WriteInt32(challenge);
        Send(socket, remoteEndpoint, writer.ToArray());
    }

    private int CreateChallenge(IPEndPoint remoteEndpoint)
    {
        long nowTicks = DateTime.UtcNow.Ticks;
        RemoveExpiredChallenges(nowTicks);
        if (_challenges.Count >= MaximumChallenges)
        {
            IPEndPoint? oldestEndpoint = null;
            long oldestIssuedTicks = long.MaxValue;
            foreach (KeyValuePair<IPEndPoint, ChallengeEntry> pair in _challenges)
            {
                if (pair.Value.IssuedTicks < oldestIssuedTicks)
                {
                    oldestEndpoint = pair.Key;
                    oldestIssuedTicks = pair.Value.IssuedTicks;
                }
            }

            if (oldestEndpoint != null)
            {
                _challenges.Remove(oldestEndpoint);
            }
        }

        int value;
        var bytes = new byte[4];
        do
        {
            _random.GetBytes(bytes);
            value = ReadInt32(bytes, 0);
        }
        while (value == 0 || value == -1);

        var endpointKey = new IPEndPoint(remoteEndpoint.Address, remoteEndpoint.Port);
        _challenges[endpointKey] = new ChallengeEntry(
            value,
            nowTicks,
            nowTicks + ChallengeLifetimeTicks);
        return value;
    }

    private void RemoveExpiredChallenges(long nowTicks)
    {
        if (_challenges.Count == 0)
        {
            return;
        }

        var expiredEndpoints = new List<IPEndPoint>();
        foreach (KeyValuePair<IPEndPoint, ChallengeEntry> pair in _challenges)
        {
            if (pair.Value.ExpiresTicks <= nowTicks)
            {
                expiredEndpoints.Add(pair.Key);
            }
        }

        for (int index = 0; index < expiredEndpoints.Count; index++)
        {
            _challenges.Remove(expiredEndpoints[index]);
        }
    }

    private static bool HasPrefix(byte[] datagram)
    {
        for (int index = 0; index < RequestPrefix.Length; index++)
        {
            if (datagram[index] != RequestPrefix[index])
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesInfoRequestName(byte[] datagram)
    {
        for (int index = 0; index < InfoRequestName.Length; index++)
        {
            if (datagram[index + 5] != InfoRequestName[index])
            {
                return false;
            }
        }

        return true;
    }

    private static int ReadInt32(byte[] bytes, int offset)
    {
        uint value = bytes[offset] |
                     ((uint)bytes[offset + 1] << 8) |
                     ((uint)bytes[offset + 2] << 16) |
                     ((uint)bytes[offset + 3] << 24);
        return unchecked((int)value);
    }

    private static byte ClampByte(int value)
    {
        return (byte)Math.Min(255, Math.Max(0, value));
    }

    private static void Send(UdpClient socket, IPEndPoint remoteEndpoint, byte[] response)
    {
        socket.Send(response, response.Length, remoteEndpoint);
    }

    private void CloseSocket()
    {
        UdpClient? socket = _socket;
        _socket = null;
        if (socket == null)
        {
            return;
        }

        try
        {
            socket.Close();
        }
        catch (ObjectDisposedException)
        {
            // The socket is already closed.
        }
    }

    private void LogWorkerFailure(string message)
    {
        if (_workerWarningLogged)
        {
            return;
        }

        _workerWarningLogged = true;
        _log.Warning($"[Query] A2S responder worker error: {message}");
    }

    private sealed class ChallengeEntry
    {
        public ChallengeEntry(int value, long issuedTicks, long expiresTicks)
        {
            Value = value;
            IssuedTicks = issuedTicks;
            ExpiresTicks = expiresTicks;
        }

        public int Value { get; }

        public long IssuedTicks { get; }

        public long ExpiresTicks { get; }
    }

    private sealed class PacketWriter : IDisposable
    {
        private readonly MemoryStream _stream = new();

        public void WriteHeader(byte responseType)
        {
            WriteByte(0xff);
            WriteByte(0xff);
            WriteByte(0xff);
            WriteByte(0xff);
            WriteByte(responseType);
        }

        public void WriteByte(byte value)
        {
            _stream.WriteByte(value);
        }

        public void WriteInt16(short value)
        {
            ushort unsigned = unchecked((ushort)value);
            WriteByte((byte)unsigned);
            WriteByte((byte)(unsigned >> 8));
        }

        public void WriteInt32(int value)
        {
            uint unsigned = unchecked((uint)value);
            WriteByte((byte)unsigned);
            WriteByte((byte)(unsigned >> 8));
            WriteByte((byte)(unsigned >> 16));
            WriteByte((byte)(unsigned >> 24));
        }

        public void WriteUInt64(ulong value)
        {
            for (int shift = 0; shift < 64; shift += 8)
            {
                WriteByte((byte)(value >> shift));
            }
        }

        public void WriteSingle(float value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            _stream.Write(bytes, 0, bytes.Length);
        }

        public void WriteString(string value)
        {
            string safeValue = (value ?? string.Empty).Replace('\0', ' ');
            byte[] bytes = Encoding.UTF8.GetBytes(safeValue);
            _stream.Write(bytes, 0, bytes.Length);
            WriteByte(0);
        }

        public byte[] ToArray()
        {
            return _stream.ToArray();
        }

        public void Dispose()
        {
            _stream.Dispose();
        }
    }
}
