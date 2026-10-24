using System.Net;
using System.Net.Sockets;

namespace Specrig;

sealed class Gate : IDisposable
{
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly List<Socket> open = [];
    readonly string host;
    readonly int port;
    bool closed;

    public Gate(string host, int port)
    {
        (this.host, this.port) = (host, port);
        listener.Start();
        _ = Accept();
    }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

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

    public void Dispose() => Close();

    async Task Accept()
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
