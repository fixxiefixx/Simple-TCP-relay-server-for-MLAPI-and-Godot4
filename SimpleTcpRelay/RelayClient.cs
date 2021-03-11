using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SimpleTcpRelay
{
    public class RelayClient
    {

        private const int RECEIVE_BUFFER_SIZE = 1024;

        private TcpClient tcpClient;
        private Thread listenThread = null;
        private Thread sendThread = null;
        private volatile bool sendThreadRunning = false;

        private Queue<byte[]> sendQueue = new Queue<byte[]>();
        private AutoResetEvent sendWaitHandle = new AutoResetEvent(false);

        public int ClientId = -1;

        public enum CommandType
        {
            StartHost=0,
            StartClient=1,
            Disconnect=2,
            SendToClient=3,
            SendToClientsExcept=4,
            Ping=5,
            Pong=6,
            StartHostResponse=7,
            StartClientResponse=8,
            DisconnectRemoteClient=9,
            ClientConnected=10
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

        public void Start()
        {
            if(listenThread!=null)
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
        }

        public void Stop()
        {
            if(listenThread==null)
            {
                throw new Exception("RelayClient not started");
            }
            tcpClient.Close();
            listenThread.Join();

            sendThreadRunning = false;
            sendWaitHandle.Set();
            sendThread.Join();

            tcpClient.Dispose();
            tcpClient = null;
            listenThread = null;
        }

        private void SendStartHostResponse(int ClientId,int roomId)
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

        private void HandleStartHost()
        {
            if (ClientId != -1)
                throw new Exception("Client is already in a room");
            lock(Program.roomManager)
            {
                int roomId = Program.roomManager.CreateRoom(out ClientId, this);
                SendStartHostResponse(ClientId, roomId);
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

        private void HandleStartClient(int roomId)
        {
            lock(Program.roomManager)
            {
                RoomManager.Room room = Program.roomManager.GetRoom(roomId);
                ClientId = Program.roomManager.AddClientToRoom(this, roomId);
                RelayClient hostClient = room.clients[room.HostClientId];
                hostClient.SendClientConnected(ClientId);
            }
        }

        private void HandlePacketData(byte[] data)
        {
            CommandType cmdType = (CommandType)data[4];
            switch(cmdType)
            {
                case CommandType.StartHost:
                    {
                        HandleStartHost();
                    }break;
                case CommandType.Ping:
                    {
                        HandlePing();
                    }break;
                case CommandType.StartClient:
                    {
                        int roomId = BitConverter.ToInt32(data, 5);
                        HandleStartClient(roomId);
                    }break;
            }
        }

        private void listenThreadFunc()
        {
            tcpClient.Client.Blocking = true;

            byte[] receiveBuffer = new byte[RECEIVE_BUFFER_SIZE];
            List<byte> ongoingPacketData = new List<byte>();
            int packetSize = 0;
            while(true)
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
                        HandlePacketData(packetData);

                        int moveCnt = ongoingPacketData.Count - packetSize;
                        if (moveCnt > 0)
                        {
                            List<byte> tmpData = new List<byte>();
                            for (int i = 0; i < moveCnt; i++)
                            {
                                tmpData.Add(ongoingPacketData[i - packetSize]);
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
            while(sendThreadRunning)
            {
                List<byte[]> dataToSend = new List<byte[]>();
                lock(sendQueue)
                {
                    while(sendQueue.Count > 0)
                    {
                        byte[] data = sendQueue.Dequeue();
                        dataToSend.Add(data);
                    }
                }
                foreach(byte[] data in dataToSend)
                {
                    tcpClient.Client.Send(data);
                }
                sendWaitHandle.WaitOne();
            }
        }


    }
}
