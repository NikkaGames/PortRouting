using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

public sealed class UdpPortForwarder
{
    private sealed class RelaySnapshot
    {
        public string ClientEndpoint;
        public string RelayEndpoint;
        public string TargetEndpoint;
        public long ClientToServerBytes;
        public long ServerToClientBytes;
        public long ClientToServerPackets;
        public long ServerToClientPackets;
        public DateTime LastSeenUtc;
    }

    private sealed class Relay
    {
        private readonly UdpClient _toServer;
        private readonly UdpClient _listener;
        private readonly IPEndPoint _client;
        private readonly IPEndPoint _target;
        private readonly string _key;
        private readonly ConcurrentDictionary<string, Relay> _map;
        private long _lastSeenTicks;
        private long _clientToServerBytes;
        private long _serverToClientBytes;
        private long _clientToServerPackets;
        private long _serverToClientPackets;

        public Relay(UdpClient listener, IPEndPoint client, IPEndPoint target, string key, ConcurrentDictionary<string, Relay> map)
        {
            _listener = listener;
            _client = client;
            _target = target;
            _key = key;
            _map = map;
            _lastSeenTicks = DateTime.UtcNow.Ticks;
            _toServer = new UdpClient(0);
            _toServer.Connect(target);

            Thread t = new Thread(new ThreadStart(RunServerToClientLoop));
            t.IsBackground = true;
            t.Start();
        }

        private void RunServerToClientLoop()
        {
            try
            {
                ServerToClientLoop().GetAwaiter().GetResult();
            }
            catch
            {
            }
        }

        public void Touch()
        {
            Interlocked.Exchange(ref _lastSeenTicks, DateTime.UtcNow.Ticks);
        }

        public bool IsExpired(TimeSpan idleTimeout)
        {
            return DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastSeenTicks) > idleTimeout.Ticks;
        }

        public async Task SendToServer(byte[] buffer)
        {
            Touch();
            int sent = await _toServer.SendAsync(buffer, buffer.Length).ConfigureAwait(false);
            Interlocked.Add(ref _clientToServerBytes, sent);
            Interlocked.Increment(ref _clientToServerPackets);
        }

        private async Task ServerToClientLoop()
        {
            try
            {
                while (true)
                {
                    UdpReceiveResult result = await _toServer.ReceiveAsync().ConfigureAwait(false);
                    int sent = await _listener.SendAsync(result.Buffer, result.Buffer.Length, _client).ConfigureAwait(false);
                    Interlocked.Add(ref _serverToClientBytes, sent);
                    Interlocked.Increment(ref _serverToClientPackets);
                    Touch();
                }
            }
            catch
            {
            }
            finally
            {
                Relay removed;
                _map.TryRemove(_key, out removed);
                try { _toServer.Close(); } catch { }
            }
        }

        public RelaySnapshot Snapshot()
        {
            string relayEndpoint = "-";
            try
            {
                EndPoint ep = _toServer.Client.LocalEndPoint;
                if (ep != null) relayEndpoint = ep.ToString();
            }
            catch
            {
            }

            return new RelaySnapshot
            {
                ClientEndpoint = _client.ToString(),
                RelayEndpoint = relayEndpoint,
                TargetEndpoint = _target.ToString(),
                ClientToServerBytes = Interlocked.Read(ref _clientToServerBytes),
                ServerToClientBytes = Interlocked.Read(ref _serverToClientBytes),
                ClientToServerPackets = Interlocked.Read(ref _clientToServerPackets),
                ServerToClientPackets = Interlocked.Read(ref _serverToClientPackets),
                LastSeenUtc = new DateTime(Interlocked.Read(ref _lastSeenTicks), DateTimeKind.Utc)
            };
        }

        public void Close()
        {
            try { _toServer.Close(); } catch { }
        }
    }

    private readonly UdpClient _listener;
    private readonly IPEndPoint _target;
    private readonly ConcurrentDictionary<string, Relay> _clients = new ConcurrentDictionary<string, Relay>();
    private readonly TimeSpan _idleTimeout = TimeSpan.FromMinutes(2);
    private readonly object _consoleLock = new object();

    public UdpPortForwarder(string listenIp, int listenPort, string targetIp, int targetPort)
    {
        _listener = new UdpClient(new IPEndPoint(IPAddress.Parse(listenIp), listenPort));
        _target = new IPEndPoint(IPAddress.Parse(targetIp), targetPort);

        Thread cleanupThread = new Thread(new ThreadStart(CleanupLoop));
        cleanupThread.IsBackground = true;
        cleanupThread.Start();

        Thread statusThread = new Thread(new ThreadStart(StatusLoop));
        statusThread.IsBackground = true;
        statusThread.Start();
    }

    public async Task Run()
    {
        while (true)
        {
            try
            {
                UdpReceiveResult packet = await _listener.ReceiveAsync().ConfigureAwait(false);
                string key = packet.RemoteEndPoint.Address + ":" + packet.RemoteEndPoint.Port;
                Relay relay = _clients.GetOrAdd(key, k => new Relay(_listener, packet.RemoteEndPoint, _target, k, _clients));
                await relay.SendToServer(packet.Buffer).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private void CleanupLoop()
    {
        while (true)
        {
            Thread.Sleep(5000);
            foreach (KeyValuePair<string, Relay> pair in _clients)
            {
                if (pair.Value.IsExpired(_idleTimeout))
                {
                    pair.Value.Close();
                }
            }
        }
    }

    private void StatusLoop()
    {
        Dictionary<string, RelaySnapshot> previous = new Dictionary<string, RelaySnapshot>();

        try { Console.CursorVisible = false; } catch { }

        while (true)
        {
            try
            {
                List<RelaySnapshot> snapshots = _clients.Values.Select(x => x.Snapshot()).OrderBy(x => x.ClientEndpoint).ToList();
                DateTime nowUtc = DateTime.UtcNow;
                Dictionary<string, RelaySnapshot> nextPrevious = snapshots.ToDictionary(x => x.ClientEndpoint, x => x);

                lock (_consoleLock)
                {
                    Console.Clear();
                    Console.WriteLine("UDP port forwarder");
                    Console.WriteLine("Listen: " + _listener.Client.LocalEndPoint);
                    Console.WriteLine("Target: " + _target);
                    Console.WriteLine("Connected clients: " + snapshots.Count);
                    Console.WriteLine("Updated: " + nowUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                    Console.WriteLine();

                    if (snapshots.Count == 0)
                    {
                        Console.WriteLine("No active clients.");
                    }
                    else
                    {
                        Console.WriteLine(
                            Fit("Client", 24) +
                            Fit("Relay", 24) +
                            Fit("Target", 24) +
                            Fit("C->S Total", 16, false) +
                            Fit("S->C Total", 16, false) +
                            Fit("C->S/s", 12, false) +
                            Fit("S->C/s", 12, false) +
                            Fit("Idle", 8, false)
                        );
                        Console.WriteLine(new string('-', 136));

                        foreach (RelaySnapshot snapshot in snapshots)
                        {
                            RelaySnapshot prev;
                            previous.TryGetValue(snapshot.ClientEndpoint, out prev);

                            long c2sRate = prev == null ? 0 : Math.Max(0, snapshot.ClientToServerBytes - prev.ClientToServerBytes);
                            long s2cRate = prev == null ? 0 : Math.Max(0, snapshot.ServerToClientBytes - prev.ServerToClientBytes);
                            TimeSpan idle = nowUtc - snapshot.LastSeenUtc;

                            Console.WriteLine(
                                Fit(snapshot.ClientEndpoint, 24) +
                                Fit(snapshot.RelayEndpoint, 24) +
                                Fit(snapshot.TargetEndpoint, 24) +
                                Fit(FormatTotal(snapshot.ClientToServerBytes, snapshot.ClientToServerPackets), 16, false) +
                                Fit(FormatTotal(snapshot.ServerToClientBytes, snapshot.ServerToClientPackets), 16, false) +
                                Fit(FormatBytes(c2sRate) + "/s", 12, false) +
                                Fit(FormatBytes(s2cRate) + "/s", 12, false) +
                                Fit(((int)idle.TotalSeconds) + "s", 8, false)
                            );
                        }
                    }
                }

                previous = nextPrevious;
            }
            catch
            {
            }

            Thread.Sleep(1000);
        }
    }

    private static string Fit(string value, int width, bool padRight = true)
    {
        if (value == null) value = "";
        if (value.Length > width) return value.Substring(0, width - 1) + "…";
        return padRight ? value.PadRight(width) : value.PadLeft(width);
    }

    private static string FormatTotal(long bytes, long packets)
    {
        return FormatBytes(bytes) + "/" + packets + "p";
    }

    private static string FormatBytes(long value)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = value;
        int unit = 0;
        while (size >= 1024d && unit < units.Length - 1)
        {
            size /= 1024d;
            unit++;
        }

        if (unit == 0) return ((long)size).ToString() + units[unit];
        if (size >= 100d) return size.ToString("0") + units[unit];
        if (size >= 10d) return size.ToString("0.0") + units[unit];
        return size.ToString("0.00") + units[unit];
    }
}

static class Program
{
    public static void Main(string[] args)
    {
        Console.WriteLine("Starting UDP port forwarder...");
        UdpPortForwarder runner = new UdpPortForwarder("0.0.0.0", 19134, "127.0.0.1", 19132);
        runner.Run().GetAwaiter().GetResult();
        Console.WriteLine("UDP port forwarder stopped.");
    }
}