using System;
using System.Net;
using System.Net.Sockets;

namespace SimpleTcpRelay
{
    public class Program
    {


        public static string ListenIPAddress = "0.0.0.0";
        public static int ListenPort = 8765;

        public static RoomManager roomManager = new RoomManager();
        public static void Main(string[] args)
        {
            
            if(args.Length>=1)
            {
                ListenIPAddress = args[0];
            }
            if(args.Length>=2)
            {
                ListenPort = int.Parse(args[1]);
            }

            TcpListener listener = new TcpListener(IPAddress.Parse(ListenIPAddress), ListenPort);
            listener.Server.Blocking = true;
            listener.Start();
            Console.WriteLine("Server started on "+ListenIPAddress+" port "+ListenPort);
            while(true)
            {
                TcpClient client = listener.AcceptTcpClient();
                RelayClient rclient = new RelayClient(client);
                rclient.Start();
            }
        }
    }
}
