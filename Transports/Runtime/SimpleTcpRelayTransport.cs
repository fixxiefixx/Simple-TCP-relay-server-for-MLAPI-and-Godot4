using MLAPI.Transports;
using MLAPI.Transports.Tasks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

class SimpleTcpRelayTransport : Transport
{
    public string IpAddress = "127.0.0.1";
    public int Port = 8765;
    public int RoomIdToConnect = 0;

    private const int RECEIVE_BUFFER_SIZE = 1024;

    private Thread receiveThread = null;
    private Thread sendThread = null;
    private volatile bool started = false;
    private bool IsHost = false;
    private ulong hostClientId = 0;
    private TcpClient tcpClient = null;
    private Queue<RelayEvent> receivedEvents = new Queue<RelayEvent>();
    private Queue<byte[]> sendQueue = new Queue<byte[]>();
    private AutoResetEvent sendWaitHandle = new AutoResetEvent(false);
    private bool sendThreadRunning = false;
    private SocketTasks connectTask = null;
    private float timeSinceStartup = 0;
    private readonly Dictionary<string, byte> channelNameToId = new Dictionary<string, byte>();
    private readonly Dictionary<byte, string> channelIdToName = new Dictionary<byte, string>();
    private volatile int ClientId = -1;
    private string mlapidefaultChannel = "";
    private volatile int connectedRoomId = -1;
    private volatile Action<int[]> currentListRoomsCallback = null;
    private volatile int[] listRoomsAnswer = null;

    private class RelayEvent
    {
        public ulong ClientId;
        public string ChannelName;
        public ArraySegment<byte> payload;
        public float receiveTime;
        public NetEventType eventType;
    }

    public enum CommandType
    {
        StartHost = 0,
        StartClient = 1,
        Disconnect = 2,
        SendToClient = 3,
        SendToClientsExcept = 4,//Not used
        Ping = 5,
        Pong = 6,
        StartHostResponse = 7,
        StartClientResponse = 8,
        DisconnectRemoteClient = 9,
        ClientConnected = 10,
        ReceiveFromClient = 11,
        ClientDisconnected,
        ListRooms,
        ListRoomsResponse
    }

    public int ConnectedRoomId
    {
        get
        {
            return connectedRoomId;
        }
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
        }
        catch (Exception ex)
        {
            Debug.Log("Exception in sendThread loop: " + ex.ToString());
            HandleDisconnect();
        }
    }

    private void HandleDisconnect()
    {
        if (ClientId != -1)
        {
            RelayEvent re = new RelayEvent()
            {
                ChannelName = mlapidefaultChannel,
                ClientId = (ulong)ClientId,
                payload = new ArraySegment<byte>(),
                receiveTime = timeSinceStartup,
                eventType = NetEventType.Disconnect
            };
            lock (receivedEvents)
            {
                receivedEvents.Enqueue(re);
            }
            ClientId = -1;
        }
        if (started)
        {
            tcpClient.Dispose();
            tcpClient = null;
            started = false;
        }
    }

    private void HandleStartClientResponse(int clientId, int hostClientId)
    {
        if (connectTask != null)
        {
            this.ClientId = clientId;
            this.hostClientId = (uint)hostClientId;
            RelayEvent re = new RelayEvent()
            {
                ChannelName = mlapidefaultChannel,
                ClientId = (ulong)clientId,
                payload = new ArraySegment<byte>(),
                receiveTime = timeSinceStartup,
                eventType = NetEventType.Connect
            };
            connectTask.Tasks[0].Success = true;
            connectTask.Tasks[0].IsDone = true;
            connectTask = null;
            lock (receivedEvents)
            {
                receivedEvents.Enqueue(re);
            }
        }
    }

    private void HandleStartHostResponse(int clientId, int roomId)
    {
        if (connectTask != null)
        {
            connectedRoomId = roomId;
            this.hostClientId = (uint)clientId;
            RelayEvent re = new RelayEvent()
            {
                ChannelName = mlapidefaultChannel,
                ClientId = (ulong)clientId,
                payload = new ArraySegment<byte>(),
                receiveTime = timeSinceStartup,
                eventType = NetEventType.Connect
            };
            connectTask.Tasks[0].Success = true;
            connectTask.Tasks[0].IsDone = true;
            connectTask = null;
            /*lock (receivedEvents)
            {
                receivedEvents.Enqueue(re);
            }*/
        }
    }

    private void HandleReceiveFromClient(int fromClientId, byte channelId,ArraySegment<byte> payload)
    {
        RelayEvent re = new RelayEvent()
        {
            ChannelName = channelIdToName[channelId],
            ClientId = (ulong)fromClientId,
            payload = payload,
            receiveTime = timeSinceStartup,
            eventType = NetEventType.Data
        };
        //Debug.Log("Received payload from client " + fromClientId + " bytes: " + payload.Count+" channel: "+re.ChannelName);
        //Debug.Log("payload-data: " + BitConverter.ToString(payload.Array, payload.Offset, payload.Count));
        lock (receivedEvents)
        {
            receivedEvents.Enqueue(re);
        }
    }

    private void HandleClientDisconnected(int remoteClientId)
    {
        RelayEvent re = new RelayEvent()
        {
            ChannelName = mlapidefaultChannel,
            ClientId = (ulong)remoteClientId,
            payload = new ArraySegment<byte>(),
            receiveTime = timeSinceStartup,
            eventType = NetEventType.Disconnect
        };
        lock (receivedEvents)
        {
            receivedEvents.Enqueue(re);
        }
    }

    private void SendDisconnectRemoteClient(int clientId)
    {
        using (MemoryStream ms = new MemoryStream())
        {
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                bw.Write((byte)CommandType.DisconnectRemoteClient);
                bw.Write(clientId);
                SendPacket(ms.ToArray());
            }
        }
    }

    private void HandleClientConnected(int clientId)
    {
        RelayEvent re = new RelayEvent()
        {
            ChannelName = mlapidefaultChannel,
            ClientId = (ulong)clientId,
            payload = new ArraySegment<byte>(),
            receiveTime = timeSinceStartup,
            eventType = NetEventType.Connect
        };
        lock (receivedEvents)
        {
            receivedEvents.Enqueue(re);
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

    private void HandleListRoomsResponse(int[] roomIds)
    {
        Debug.Log("HandleListRooms response called");
        listRoomsAnswer = roomIds;
    }

    private void HandlePacketData(byte[] data)
    {
        CommandType cmdType = (CommandType)data[4];
        //Debug.Log("Received command " + cmdType.ToString() + " from relay\n"+
        //"Data: " + BitConverter.ToString(data));
        switch (cmdType)
        {
            case CommandType.StartClientResponse:
                {
                    int clientId = BitConverter.ToInt32(data, 5);
                    int hostClientId = BitConverter.ToInt32(data, 9);
                    HandleStartClientResponse(clientId, hostClientId);
                }
                break;
            case CommandType.StartHostResponse:
                {
                    int clientId = BitConverter.ToInt32(data, 5);
                    int roomId = BitConverter.ToInt32(data, 9);
                    HandleStartHostResponse(clientId, roomId);
                }
                break;
            case CommandType.ReceiveFromClient:
                {
                    int remoteClientId = BitConverter.ToInt32(data, 5);
                    byte channelId = data[6];
                    byte[] payload = new byte[data.Length - 10];
                    Array.Copy(data, 10, payload, 0, payload.Length);
                    HandleReceiveFromClient(remoteClientId,channelId ,new ArraySegment<byte>(payload));
                }
                break;
            case CommandType.ClientDisconnected:
                {
                    int remoteClientId = BitConverter.ToInt32(data, 5);
                    HandleClientDisconnected(remoteClientId);
                }
                break;
            case CommandType.ClientConnected:
                {
                    int remoteClientId = BitConverter.ToInt32(data, 5);
                    HandleClientConnected(remoteClientId);
                }
                break;
            case CommandType.Ping:
                {
                    HandlePing();
                }break;
            case CommandType.ListRoomsResponse:
                {
                    int roomIdCount = BitConverter.ToInt32(data, 5);
                    int[] roomIds = new int[roomIdCount];
                    for(int i=0;i<roomIdCount;i++)
                    {
                        roomIds[i] = BitConverter.ToInt32(data, 9 + (i * sizeof(int)));
                    }
                    HandleListRoomsResponse(roomIds);
                }break;
            default:
                {
                    Debug.Log("But no function exists for this type of message");
                }break;
        }
    }

    private void receiveThreadFunc()
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
                        }
                        catch (Exception ex)
                        {
                            Debug.Log("Exception while handling packet in listener thread: " + ex.ToString());
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
        }
        catch (Exception ex)
        {
            Debug.Log("Exception in receiveThread loop: " + ex.ToString());
            StopSocket();
        }
    }

    private void SendStartClient(int roomId)
    {
        using (MemoryStream ms = new MemoryStream())
        {
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                bw.Write((byte)CommandType.StartClient);
                bw.Write(roomId);
                SendPacket(ms.ToArray());
            }
        }
    }

    private void SendStartHost()
    {
        using (MemoryStream ms = new MemoryStream())
        {
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                bw.Write((byte)CommandType.StartHost);
                SendPacket(ms.ToArray());
            }
        }
    }

    public void SendPacket(byte[] data)
    {
        if (!started)
            throw new Exception("Socket not started");
        byte[] dataToSend = new byte[sizeof(int) + data.Length];
        byte[] sizeData = BitConverter.GetBytes((int)dataToSend.Length);
        Array.Copy(sizeData, dataToSend, sizeData.Length);
        Array.Copy(data, 0, dataToSend, sizeData.Length, data.Length);
        lock (sendQueue)
        {
            sendQueue.Enqueue(dataToSend);
        }
        sendWaitHandle.Set();
    }



    private void StartSocket()
    {
        if (started)
        {
            //throw new Exception("SimpleTcpRelayTransport already started");
            return;
        }
        started = true;
        tcpClient = new TcpClient(IpAddress, Port);
        sendThreadRunning = true;
        sendThread = new Thread(sendThreadFunc);
        sendThread.IsBackground = true;
        sendThread.Start();

        receiveThread = new Thread(receiveThreadFunc);
        receiveThread.IsBackground = true;
        receiveThread.Start();
    }

    private void StopSocket()
    {
        if (!started)
            return;
        sendThreadRunning = false;
        tcpClient.Close();
        tcpClient.Dispose();
        tcpClient = null;
        started = false;
    }

    private void SendListRooms()
    {
        SendPacket(BitConverter.GetBytes((byte)CommandType.ListRooms));
    }

    public void ListRooms(Action<int[]> listRoomsCallback)
    {
        StartSocket();
        currentListRoomsCallback = listRoomsCallback;
        SendListRooms();
    }

    public override void Send(ulong clientId, ArraySegment<byte> data, string channelName)
    {
        //Debug.Log("Sending " + data.Count + " bytes to client " + clientId + " on channel " + channelName);
        //Debug.Log("data: " + BitConverter.ToString(data.Array, data.Offset, data.Count));
        using (MemoryStream ms = new MemoryStream())
        {
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                bw.Write((byte)CommandType.SendToClient);
                bw.Write((int)clientId);
                bw.Write(channelNameToId[channelName]);
                bw.Write(data.Array,data.Offset,data.Count);
                SendPacket(ms.ToArray());
            }
        }
    }

    public override NetEventType PollEvent(out ulong clientId, out string channelName, out ArraySegment<byte> payload, out float receiveTime)
    {
        lock (receivedEvents)
        {
            if (receivedEvents.Count > 0)
            {
                RelayEvent re = receivedEvents.Dequeue();
                clientId = re.ClientId;
                channelName = re.ChannelName;
                payload = re.payload;
                receiveTime = re.receiveTime;
                return re.eventType;
            }
            else
            {
                clientId = 0;
                channelName = "";
                payload = new ArraySegment<byte>();
                receiveTime = timeSinceStartup;
                return NetEventType.Nothing;
            }
        }
    }

    public override SocketTasks StartClient()
    {
        IsHost = false;
        connectTask = SocketTask.Working.AsTasks();
        StartSocket();
        SendStartClient(RoomIdToConnect);
        return connectTask;
    }

    public override SocketTasks StartServer()
    {
        IsHost = true;
        connectTask = SocketTask.Working.AsTasks();
        StartSocket();
        SendStartHost();
        return connectTask;
    }

    public override void DisconnectRemoteClient(ulong clientId)
    {
        SendDisconnectRemoteClient((int)clientId);
    }

    public override void DisconnectLocalClient()
    {
        StopSocket();
    }

    public override ulong GetCurrentRtt(ulong clientId)
    {
        //Not implemented yet.
        return 0;
    }

    public override void Shutdown()
    {
        if (started)
            StopSocket();
        channelIdToName.Clear();
        channelNameToId.Clear();
    }

    public override void Init()
    {
        for (byte i = 0; i < MLAPI_CHANNELS.Length; i++)
        {
            channelIdToName.Add(i, MLAPI_CHANNELS[i].Name);
            channelNameToId.Add(MLAPI_CHANNELS[i].Name, i);
        }
        mlapidefaultChannel = channelIdToName[0];
    }

    private void Update()
    {
        timeSinceStartup = Time.realtimeSinceStartup;
        if(listRoomsAnswer!=null && currentListRoomsCallback!=null)
        {
            currentListRoomsCallback.Invoke(listRoomsAnswer);
            listRoomsAnswer = null;
            currentListRoomsCallback = null;
        }

    }

    public override ulong ServerClientId { get { return hostClientId; } }
}
