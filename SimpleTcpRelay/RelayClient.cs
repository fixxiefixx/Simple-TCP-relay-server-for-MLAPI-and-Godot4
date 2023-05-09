using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SimpleTcpRelay
{
    public class RelayClient
    {

        private const int RECEIVE_BUFFER_SIZE = 1024;
        private const int PING_TIMEOUT = 5 * 1000;
        private const int PING_TIMEOUT_WARNING = 25 * 1000;
        private TcpClient tcpClient;
        private Thread listenThread = null;
        private Thread sendThread = null;
        private Thread pingThread = null;
        private volatile bool sendThreadRunning = false;
        private volatile bool disconnected = false;
        private volatile bool ponged = false;

        private Queue<byte[]> sendQueue = new Queue<byte[]>();
        private AutoResetEvent sendWaitHandle = new AutoResetEvent(false);

        public volatile int ClientId = -1;
        public volatile string ConnectedRoomId = null;
        private volatile bool stopped = false;
        public volatile bool IsHost = false;

        private object startStopBlock = new object();
       

        public enum CommandType
        {
            StartHost=0,
            StartClient=1,
            Disconnect=2,
            SendToClient=3,
            SendToClientsExcept=4,//Not used
            Ping=5,
            Pong=6,
            StartHostResponse=7,
            StartClientResponse=8,
            DisconnectRemoteClient=9,
            ClientConnected=10,
            ReceiveFromClient=11,
            ClientDisconnected,
            ListRooms,
            ListRoomsResponse,
            SetRoomVisible,//only From Host
            SendToMultipleClients,
            TimeoutWarning
        }

        public RelayClient(TcpClient tcpClient)
        {
            if (tcpClient == null)
                throw new ArgumentException("tcpClient can not be null");
            this.tcpClient = tcpClient;
            
        }

        public bool IsStarted
        {
            get
            {
                return listenThread != null;
            }
        }

        public static void SetTcpKeepAlive(Socket socket, uint keepaliveTime, uint keepaliveInterval)
        {
            /* the native structure
            struct tcp_keepalive {
            ULONG onoff;
            ULONG keepalivetime;
            ULONG keepaliveinterval;
            };
            */

            // marshal the equivalent of the native structure into a byte array
            uint dummy = 0;
            byte[] inOptionValues = new byte[Marshal.SizeOf(dummy) * 3];
            BitConverter.GetBytes((uint)(keepaliveTime)).CopyTo(inOptionValues, 0);
            BitConverter.GetBytes((uint)keepaliveTime).CopyTo(inOptionValues, Marshal.SizeOf(dummy));
            BitConverter.GetBytes((uint)keepaliveInterval).CopyTo(inOptionValues, Marshal.SizeOf(dummy) * 2);

            // write SIO_VALS to Socket IOControl
            socket.IOControl(IOControlCode.KeepAliveValues, inOptionValues, null);
        }

        public void Start()
        {
            Console.WriteLine("Client starting");
            lock (startStopBlock)
            {
                //SetTcpKeepAlive(tcpClient.Client, 5000, 5000);
                tcpClient.Client.Blocking = true;
                if (listenThread != null)
                {
                    throw new Exception("RelayClient already started");
                }

                listenThread = new Thread(listenThreadFunc);
                listenThread.IsBackground = true;
                listenThread.Start();

                sendThread = new Thread(sendThreadFunc);
                sendThread.IsBackground = true;
                sendThreadRunning = true;
                sendThread.Start();

                pingThread = new Thread(pingThreadFunc);
                pingThread.IsBackground = true;
                pingThread.Start();
            }
        }

        public void Stop()
        {
            if (stopped)
                return;
            stopped = true;
            Console.WriteLine("Stopping client: "+ClientId);
            lock (startStopBlock)
            {
                if (listenThread == null)
                {
                    Console.WriteLine("Client already stopped");
                    return;
                }



                tcpClient.Close();
                //listenThread.Join();

                sendThreadRunning = false;
                sendWaitHandle.Set();
                //sendThread.Join();

                tcpClient.Dispose();
                tcpClient = null;
                listenThread = null;
            }
           

            try
            {
                HandleDisconnect();
            }catch(Exception ex)
            {
                Console.WriteLine("Exception while disconnectiong client: "+ex.ToString());
            }
        }

        private void SendStartHostResponse(int ClientId,string roomId)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                using (BinaryWriter bw = new BinaryWriter(ms))
                {
                    bw.Write((byte)CommandType.StartHostResponse);
                    bw.Write(ClientId);
                    bw.Write(roomId);
                    SendPacket(ms.ToArray());
                }
               
            }
        }           

        private void HandleStartHost(string password, string name, string gameName)
        {
            if (ClientId != -1)
                throw new Exception("Client is already in a room");
            IsHost = true;
            lock(Program.roomManager)
            {
                int clientIdret;
                ConnectedRoomId = Program.roomManager.CreateRoom(out clientIdret, this, password, name, gameName);
                ClientId = clientIdret;
                SendStartHostResponse(ClientId, ConnectedRoomId);
            }
        }

        private void SendPong()
        {
            SendPacket(BitConverter.GetBytes((byte)CommandType.Pong));
        }

        private void HandlePing()
        {
            SendPong();
        }

        private void SendPing()
        {
            SendPacket(BitConverter.GetBytes((byte)CommandType.Ping));
        }

        private void SendTimeoutWarning()
        {
            SendPacket(BitConverter.GetBytes((byte)CommandType.TimeoutWarning));
        }


        private void HandlePong()
        {
            ponged = true;
        }

        public void SendClientConnected(int clientId)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                using (BinaryWriter bw = new BinaryWriter(ms))
                {
                    bw.Write((byte)CommandType.ClientConnected);
                    bw.Write(clientId);
                    SendPacket(ms.ToArray());
                }
            }
        }

        private void SendStartClientResponse(int clientId, int hostClientId)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                using (BinaryWriter bw = new BinaryWriter(ms))
                {
                    bw.Write((byte)CommandType.StartClientResponse);
                    bw.Write(clientId);
                    bw.Write(hostClientId);
                    SendPacket(ms.ToArray());
                }
            }
        }

        private void HandleStartClient(string roomId, string password)
        {
            ConnectedRoomId = roomId;
            lock(Program.roomManager)
            {
                RoomManager.Room room = Program.roomManager.GetRoom(roomId);
                if (room.Password == password)
                {
                    ClientId = Program.roomManager.AddClientToRoom(this, roomId);
                    RelayClient hostClient = room.clients[room.HostClientId];
                    hostClient.SendClientConnected(ClientId);
                    SendStartClientResponse(ClientId, room.HostClientId);
                }else
                {
                    Console.WriteLine("Client connected with wrong password");
                    HandleDisconnect();
                }
            }
        }

        private void HandleSendToClient(int toClientId, byte channelId, byte[] payload)
        {
            lock (Program.roomManager)
            {
                RoomManager.Room room = Program.roomManager.GetRoom(ConnectedRoomId);
                if (toClientId == -1)
                {
                    foreach(RelayClient targetClient in room.clients.Values)
                    {
                        if (targetClient == this)
                            continue;
                        using (MemoryStream ms = new MemoryStream())
                        {
                            using (BinaryWriter bw = new BinaryWriter(ms))
                            {
                                bw.Write((byte)CommandType.ReceiveFromClient);
                                bw.Write(ClientId);
                                bw.Write(channelId);
                                bw.Write(payload);
                                targetClient.SendPacket(ms.ToArray());
                            }
                        }

                    }
                }
                else
                {


                    RelayClient targetClient = room.clients[toClientId];

                    using (MemoryStream ms = new MemoryStream())
                    {
                        using (BinaryWriter bw = new BinaryWriter(ms))
                        {
                            bw.Write((byte)CommandType.ReceiveFromClient);
                            bw.Write(ClientId);
                            bw.Write(channelId);
                            bw.Write(payload);
                            targetClient.SendPacket(ms.ToArray());
                        }
                    }
                }
            }
        }

        private void HandleSendToMultipleClients(int[] toClientIds, byte channelId, byte[] payload)
        {
            lock (Program.roomManager)
            {
                RoomManager.Room room = Program.roomManager.GetRoom(ConnectedRoomId);
                using (MemoryStream ms = new MemoryStream())
                {
                    using (BinaryWriter bw = new BinaryWriter(ms))
                    {
                        bw.Write((byte)CommandType.ReceiveFromClient);
                        bw.Write(ClientId);
                        bw.Write(channelId);
                        bw.Write(payload);
                        foreach (int toClientId in toClientIds)
                        {
                            RelayClient targetClient = room.clients[toClientId];
                            targetClient.SendPacket(ms.ToArray());
                        }
                    }
                }
            }
        }

        public void SendClientDisconnected(int clientId)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                using (BinaryWriter bw = new BinaryWriter(ms))
                {
                    bw.Write((byte)CommandType.ClientDisconnected);
                    bw.Write(clientId);
                    SendPacket(ms.ToArray());
                }
            }
        }

        private void HandleDisconnect()
        {
            if(!disconnected && ConnectedRoomId != null)
            {
                disconnected = true;
                List<RelayClient> clientsToStop = new List<RelayClient>();
                lock (Program.roomManager)
                {
                    RoomManager.Room room = Program.roomManager.GetRoom(ConnectedRoomId);
                    if (IsHost)
                    {
                        
                        foreach(RelayClient client in room.clients.Values)
                        {
                            if (client != this && ClientId != -1)
                                client.SendClientDisconnected(ClientId);
                            clientsToStop.Add(client);

                        }
                        Program.roomManager.DestroyRoom(ConnectedRoomId);
                    }
                    else
                    {
                        RelayClient hostClient = room.clients[room.HostClientId];
                        room.clients.Remove(ClientId);
                        hostClient.SendClientDisconnected(ClientId);
                        clientsToStop.Add(this);
                    }
                    ConnectedRoomId = null;
                }

                if (clientsToStop.Count > 0)
                {
                    Thread.Sleep(200);

                    foreach (RelayClient client in clientsToStop)
                    {
                        client.Stop();
                    }
                }
            }
        }

        private void HandleDisconnectRemoteClient(int remoteClientId)
        {
            if (!IsHost)
                throw new Exception("client that is not host tried to disconnect remote client");
            RelayClient clientToDisconnect = null;
            lock(Program.roomManager)
            {
                RoomManager.Room room = Program.roomManager.GetRoom(ConnectedRoomId);
                clientToDisconnect = room.clients[remoteClientId];
            }

            if(clientToDisconnect!=null)
            {
                clientToDisconnect.Stop();
            }
        }

        private void HandleListRooms(string gameName)
        {
            List<RoomManager.Room> roomsToReturn = new List<RoomManager.Room>();
            RoomManager.Room[] rooms;
            lock (Program.roomManager)
            {
                rooms = Program.roomManager.GetRooms();

                foreach (RoomManager.Room room in rooms)
                {
                    if(room.Visible && room.GameName == gameName)
                        roomsToReturn.Add(room);
                }

                using (MemoryStream ms = new MemoryStream())
                {
                    using (BinaryWriter bw = new BinaryWriter(ms))
                    {
                        bw.Write((byte)CommandType.ListRoomsResponse);
                        bw.Write(roomsToReturn.Count);
                        foreach (RoomManager.Room room in roomsToReturn)
                        {
                            bw.Write(room.RoomId);
                            bw.Write(room.clients.Count);
                            bw.Write(room.Password != "");
                            bw.Write(room.Name);
                        }
                        SendPacket(ms.ToArray());
                    }
                }
            }
        }

        private void HandleSetRoomVisible(bool visible)
        {
            if (!IsHost)
                return;
            lock (Program.roomManager)
            {
                RoomManager.Room room = Program.roomManager.GetRoom(ConnectedRoomId);
                room.Visible = visible;
            }

        }

        private void HandlePacketData(byte[] data)
        {
            //Thread.Sleep(200);//Warning: just for test bad network latency
            CommandType cmdType = (CommandType)data[4];
            if (cmdType != CommandType.SendToClient && cmdType != CommandType.Pong
                && cmdType != CommandType.SendToMultipleClients)
            {
                Console.WriteLine("Received command " + cmdType.ToString() + " from Client " + ClientId);
                Console.WriteLine("Data: " + BitConverter.ToString(data));
            }
            switch(cmdType)
            {
                case CommandType.StartHost:
                    {
                        using(MemoryStream ms = new MemoryStream(data))
                        {
                            ms.Seek(5, SeekOrigin.Begin);
                            using(BinaryReader br=new BinaryReader(ms))
                            {
                                string password = br.ReadString();
                                string name = br.ReadString();
                                string gameName = br.ReadString();
                                HandleStartHost(password,name,gameName);
                            }
                        }
                        
                    }break;
                case CommandType.Ping:
                    {
                        HandlePing();
                    }break;
                case CommandType.StartClient:
                    {
                        using (MemoryStream ms = new MemoryStream(data))
                        {
                            ms.Seek(5, SeekOrigin.Begin);
                            using (BinaryReader br = new BinaryReader(ms))
                            {
                                string roomId = br.ReadString();
                                string password = br.ReadString();
                                HandleStartClient(roomId,password);
                            }
                        }
                    }break;
                case CommandType.SendToClient:
                    {
                        int toClientId= BitConverter.ToInt32(data, 5);
                        byte channelId = data[9];
                        byte[] payload = new byte[data.Length - 10];
                        
                        Array.Copy(data, 10, payload, 0, payload.Length);
                        HandleSendToClient(toClientId, channelId, payload);
                    }break;
                case CommandType.SendToMultipleClients:
                    {
                        using (MemoryStream ms = new MemoryStream(data))
                        {
                            ms.Seek(5, SeekOrigin.Begin);
                            using (BinaryReader br = new BinaryReader(ms))
                            {
                                int clientCount = br.ReadInt32();
                                int[] toClientIds = new int[clientCount];
                                for (int i = 0; i < clientCount; i++)
                                {
                                    toClientIds[i] = br.ReadInt32();
                                }

                                byte channelId = br.ReadByte();
                                byte[] payload = new byte[data.Length - (10+(clientCount*4))];

                                Array.Copy(data, (10 + (clientCount * 4)), payload, 0, payload.Length);
                                HandleSendToMultipleClients(toClientIds, channelId, payload);
                            }
                        }
                    
                    }
                    break;
                case CommandType.Disconnect:
                    {
                        HandleDisconnect();
                    }break;
                case CommandType.DisconnectRemoteClient:
                    {
                        int remoteClientId = BitConverter.ToInt32(data, 5);
                        HandleDisconnectRemoteClient(remoteClientId);
                    }break;
                case CommandType.Pong:
                    {
                        HandlePong();
                    }
                    break;
                case CommandType.ListRooms:
                    {
                        using (MemoryStream ms = new MemoryStream(data))
                        {
                            ms.Seek(5, SeekOrigin.Begin);
                            using (BinaryReader br = new BinaryReader(ms))
                            {
                                string gameName = br.ReadString();
                                HandleListRooms(gameName);
                            }
                        }
                    }break;
                case CommandType.SetRoomVisible:
                    {
                        bool visible = BitConverter.ToBoolean(data, 5);
                        HandleSetRoomVisible(visible);
                    }break;
            }
        }

        private void pingThreadFunc()
        {
            try
            {
                while (sendThreadRunning)
                {
                    ponged = false;
                    SendPing();
                    Thread.Sleep(PING_TIMEOUT);
                    if (!ponged)
                    {
                        SendTimeoutWarning();
                        Thread.Sleep(PING_TIMEOUT_WARNING);
                        if (!ponged)
                        {
                            Console.WriteLine("Ping timeout for client " + ClientId);
                            Stop();
                        }
                    }
                }
            }catch(Exception ex)
            {
                Console.WriteLine("Exception in pingThread loop: " + ex.ToString());
                HandleDisconnect();
            }
        }
        private void listenThreadFunc()
        {
            tcpClient.Client.Blocking = true;

            byte[] receiveBuffer = new byte[RECEIVE_BUFFER_SIZE];
            List<byte> ongoingPacketData = new List<byte>();
            int packetSize = 0;
            try
            {
                while (true)
                {
                    int readCount = tcpClient.Client.Receive(receiveBuffer);
                    if (readCount == 0)
                        break;

                    for (int i = 0; i < readCount; i++)
                    {
                        ongoingPacketData.Add(receiveBuffer[i]);
                    }
                    bool weiter = true;
                    while (weiter)
                    {
                        if (packetSize == 0)
                        {
                            if (ongoingPacketData.Count >= sizeof(int))
                            {
                                byte[] packetSizeBuffer = new byte[sizeof(int)];
                                int bytesFromOngoingPacketData = Math.Min(ongoingPacketData.Count, packetSizeBuffer.Length);

                                for (int i = 0; i < bytesFromOngoingPacketData; i++)
                                {
                                    packetSizeBuffer[i] = ongoingPacketData[i];
                                }
                                packetSize = BitConverter.ToInt32(packetSizeBuffer, 0);
                            }
                        }



                        if (packetSize > 0 && ongoingPacketData.Count >= packetSize)
                        {
                            byte[] packetData = new byte[packetSize];
                            for (int i = 0; i < packetSize; i++)
                            {
                                packetData[i] = ongoingPacketData[i];
                            }
                            try
                            {
                                HandlePacketData(packetData);
                            }catch(Exception ex)
                            {
                                Console.WriteLine("Exception while handling packet in listener thread: "+ex.ToString());
                            }

                            int moveCnt = ongoingPacketData.Count - packetSize;
                            if (moveCnt > 0)
                            {
                                List<byte> tmpData = new List<byte>();
                                for (int i = 0; i < moveCnt; i++)
                                {
                                    tmpData.Add(ongoingPacketData[i + packetSize]);
                                }
                                ongoingPacketData = tmpData;
                            }
                            else
                            {
                                ongoingPacketData.Clear();
                            }
                            packetSize = 0;
                        }
                        else
                        {
                            weiter = false;
                        }
                    }
                }
            }catch(Exception ex)
            {
                Console.WriteLine("Exception in listenThread loop: "+ex.ToString());
                Stop();
            }
        }

        public void SendPacket(byte[] data)
        {
            byte[] dataToSend = new byte[sizeof(int) + data.Length];
            byte[] sizeData = BitConverter.GetBytes((int)dataToSend.Length);
            Array.Copy(sizeData, dataToSend, sizeData.Length);
            Array.Copy(data,0,dataToSend,sizeData.Length,data.Length);
            lock(sendQueue)
            {
                sendQueue.Enqueue(dataToSend);
            }
            sendWaitHandle.Set();
        }

        private void sendThreadFunc()
        {
            try
            {
                while (sendThreadRunning)
                {
                    List<byte[]> dataToSend = new List<byte[]>();
                    lock (sendQueue)
                    {
                        while (sendQueue.Count > 0)
                        {
                            byte[] data = sendQueue.Dequeue();
                            dataToSend.Add(data);
                        }
                    }
                    foreach (byte[] data in dataToSend)
                    {
                        tcpClient.Client.Send(data);
                    }
                    sendWaitHandle.WaitOne();
                }
            }catch(Exception ex)
            {
                Console.WriteLine("Exception in sendThread loop: " + ex.ToString());
                Stop();
            }
        }


    }
}
