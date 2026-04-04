using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

public sealed class UdpPortForwarder
{
    private sealed class Relay
    {
        private readonly UdpClient _toServer;
        private readonly UdpClient _listener;
        private readonly IPEndPoint _client;
        private readonly string _key;
        private readonly ConcurrentDictionary<string, Relay> _map;
        private DateTime _lastSeenUtc;

        public Relay(UdpClient listener, IPEndPoint client, IPEndPoint target, string key, ConcurrentDictionary<string, Relay> map)
        {
            _listener = listener;
            _client = client;
            _key = key;
            _map = map;
            _lastSeenUtc = DateTime.UtcNow;
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
            _lastSeenUtc = DateTime.UtcNow;
        }

        public bool IsExpired(TimeSpan idleTimeout)
        {
            return DateTime.UtcNow - _lastSeenUtc > idleTimeout;
        }

        public Task SendToServer(byte[] buffer)
        {
            Touch();
            return _toServer.SendAsync(buffer, buffer.Length);
        }

        private async Task ServerToClientLoop()
        {
            try
            {
                while (true)
                {
                    UdpReceiveResult result = await _toServer.ReceiveAsync().ConfigureAwait(false);
                    await _listener.SendAsync(result.Buffer, result.Buffer.Length, _client).ConfigureAwait(false);
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

        public void Close()
        {
            try { _toServer.Close(); } catch { }
        }
    }

    private readonly UdpClient _listener;
    private readonly IPEndPoint _target;
    private readonly ConcurrentDictionary<string, Relay> _clients = new ConcurrentDictionary<string, Relay>();
    private readonly TimeSpan _idleTimeout = TimeSpan.FromMinutes(2);

    public UdpPortForwarder(string listenIp, int listenPort, string targetIp, int targetPort)
    {
        _listener = new UdpClient(new IPEndPoint(IPAddress.Parse(listenIp), listenPort));
        _target = new IPEndPoint(IPAddress.Parse(targetIp), targetPort);

        Thread t = new Thread(new ThreadStart(CleanupLoop));
        t.IsBackground = true;
        t.Start();
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
            } catch { }
        }
    }

    private void CleanupLoop()
    {
        while (true)
        {
            Thread.Sleep(30000);
            foreach (var pair in _clients)
            {
                if (pair.Value.IsExpired(_idleTimeout))
                {
                    pair.Value.Close();
                }
            }
        }
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