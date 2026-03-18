using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ForzaDataOut
{
    public static class TcpClientExtensions
    {
        public static TcpState GetState(this TcpClient tcpClient)
        {
            var conn = IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpConnections()
                .SingleOrDefault(x => x.LocalEndPoint.Equals(tcpClient.Client.LocalEndPoint));
            return conn?.State ?? TcpState.Unknown;
        }
    }
}