using System.Net;
using System.Net.Sockets;

namespace Specrig;

sealed class Gate : IDisposable
{
    readonly List<Socket> open = [];
    readonly string host;
    readonly int port;
    TcpListener listener = new(IPAddress.Loopback, 0);
    bool closed;

    public Gate(string host, int port)
    {
        (this.host, this.port) = (host, port);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Accept(listener);
    }

    public int Port { get; }

    public void Close()
    {
        lock (open)
        {
            if (closed)
            {
                return;
            }
            closed = true;
            listener.Stop();
            open.ForEach(socket => socket.Close());
            open.Clear();
        }
    }

    // On the port the service was given, which is the one it still connects to.
    public void Open()
    {
        lock (open)
        {
            if (!closed)
            {
                return;
            }
            listener = new TcpListener(IPAddress.Loopback, Port);
            listener.Start();
            closed = false;
            _ = Accept(listener);
        }
    }

    public void Dispose() => Close();

    async Task Accept(TcpListener listener)
    {
        while (true)
        {
            Socket client;
            try
            {
                client = await listener.AcceptSocketAsync();
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            _ = Pipe(client);
        }
    }

    async Task Pipe(Socket client)
    {
        var upstream = new Socket(SocketType.Stream, ProtocolType.Tcp);
        lock (open)
        {
            if (closed)
            {
                client.Close();
                return;
            }
            open.Add(client);
            open.Add(upstream);
        }
        try
        {
            await upstream.ConnectAsync(host, port);
            await using var inward = new NetworkStream(client, ownsSocket: true);
            await using var outward = new NetworkStream(upstream, ownsSocket: true);
            await Task.WhenAny(inward.CopyToAsync(outward), outward.CopyToAsync(inward));
        }
        catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException)
        {
        }
        finally
        {
            client.Close();
            upstream.Close();
            lock (open)
            {
                open.Remove(client);
                open.Remove(upstream);
            }
        }
    }
}
