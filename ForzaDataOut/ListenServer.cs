using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ForzaDataOut
{
    public class ListenServer
    {
        private readonly IPAddress BindAddress;
        private readonly int Port;
        private readonly ILogger Logger;

        private readonly List<TcpClient> Clients = new List<TcpClient>();
        private readonly object ClientsLock = new object();
        private TcpListener? Server = null;
        private Task? ListenerTask = null;

        public ListenServer(string bindAddress, int port, ILogger logger)
        {
            BindAddress = string.IsNullOrWhiteSpace(bindAddress) ? IPAddress.Any : IPAddress.Parse(bindAddress);
            Port = port;
            Logger = logger;
        }

        public void Start(CancellationToken cancellationToken)
        {
            Server = new TcpListener(BindAddress, Port);
            ListenerTask = StartListener(cancellationToken);
        }

        private async Task StartListener(CancellationToken cancellationToken)
        {
            if (Server == null)
            {
                Logger.LogError($"Attempting to start ListenServer: Server is null");
                return;
            }

            try
            {
                Server.Start();
                Logger.LogInformation($"ListenServer listening on {BindAddress}:{Port}");
                while (!cancellationToken.IsCancellationRequested)
                {
                    TcpClient client = await Server.AcceptTcpClientAsync(cancellationToken);
                    _ = Task.Run(() => ClientHandler(client, cancellationToken), cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                Logger.LogInformation("ListenServer cancelled");
            }
            catch (SocketException e)
            {
                Logger.LogError(e, "SocketException:");
            }

            Logger.LogInformation("ListenServer shutting down");
            Server.Stop();
        }

        private void AddClient(TcpClient client)
        {
            lock (ClientsLock)
            {
                Clients.Add(client);
            }
        }

        private void RemoveClient(TcpClient client)
        {
            lock (ClientsLock)
            {
                Clients.Remove(client);
            }
        }

        private async Task ClientHandler(TcpClient client, CancellationToken cancellationToken)
        {
            IPEndPoint? endPoint = (IPEndPoint?)client.Client.RemoteEndPoint;
            Logger.LogInformation($"Client Connected: {endPoint?.Address}");
            AddClient(client);

            TcpState clientState = client.GetState();
            while (clientState == TcpState.Established && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, cancellationToken);
                    clientState = client.GetState();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, $"Error monitoring client {endPoint?.Address}");
                    break;
                }
            }

            if (client.GetState() == TcpState.Established)
            {
                try
                {
                    client.Close();
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, $"Error closing client {endPoint?.Address}");
                }
            }
            Logger.LogInformation($"Client Disconnected: {endPoint?.Address}");

            RemoveClient(client);
        }

        public async Task Broadcast(byte[] data)
        {
            List<TcpClient> clientsCopy;
            lock (ClientsLock)
            {
                clientsCopy = new List<TcpClient>(Clients);
            }

            foreach (TcpClient client in clientsCopy)
            {
                try
                {
                    if (client.Connected && client.GetStream().CanWrite)
                    {
                        await client.GetStream().WriteAsync(data);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, $"Error broadcasting to client");
                }
            }
        }
    }
}