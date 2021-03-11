using System;
using System.Net;
using System.Net.Sockets;

namespace SimpleTcpRelay
{
    class Program
    {
        public static string ListenIPAddress = "0.0.0.0";
        public static int ListenPort = 8765;

        public static RoomManager roomManager = new RoomManager();
        static void Main(string[] args)
        {
            
            TcpListener listener = new TcpListener(IPAddress.Parse(ListenIPAddress), ListenPort);
            listener.Server.Blocking = true;
            listener.Start();
            Console.WriteLine("Server started");
            while(true)
            {
                TcpClient client = listener.AcceptTcpClient();
                RelayClient rclient = new RelayClient(client);
                rclient.Start();
            }
        }
    }
}
