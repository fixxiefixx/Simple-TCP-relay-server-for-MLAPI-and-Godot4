using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;

public partial class SimpleRelayMultiplayerPeer : MultiplayerPeerExtension
{

    public string IpAddress = "127.0.0.1";
    public int Port = 8765;
    public string RoomIdToConnect = "";
    public string RoomPassword = "";
    public string RoomNameToCreate = "";
    public string GameName = "ExampleGame";

    private const int RECEIVE_BUFFER_SIZE = 1024;

    private Thread receiveThread = null;
    private Thread sendThread = null;
    private volatile bool started = false;
    private bool sendThreadRunning = false;
    private Queue<byte[]> sendQueue = new Queue<byte[]>();
    private AutoResetEvent sendWaitHandle = new AutoResetEvent(false);
    private TcpClient tcpClient = null;
    private volatile int ClientId = -1;
    private object startStopClientLock = new object();
    private int targetPeer = -1;
    private bool refuseNewConnections = false;
    private bool isServer = false;
    private int transferChannel = 0;
    private TransferModeEnum transferMode = TransferModeEnum.Reliable;
    private volatile RoomInfo[] listRoomsAnswer = null;
    private Queue<PacketScriptFromClient> receivedPacketScripts=new Queue<PacketScriptFromClient>();
    private volatile Action<RoomInfo[]> currentListRoomsCallback = null;
    private HashSet<int> connectedClients = new HashSet<int>();

    private class PacketScriptFromClient
    {
        public int clientId;
        public byte channel;
        public TransferModeEnum transferMode;
        public byte[] data;
        
    }

    public class RoomInfo
    {
        public string RoomId = "";
        public int ClientCount = 0;
        public bool HasPassword = false;
        public string Name = "";
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
        ListRoomsResponse,
        SetRoomVisible,//only From Host
        SendToMultipleClients
    }

    private enum SubCommandType
    {
        Payload = 0,
        ClientConnected = 1,
        ClientDisconnected = 2
    }

    public SimpleRelayMultiplayerPeer()
    {

    }

    private void StartSocket()
    {
        if (started)
        {
            //throw new Exception("SimpleTcpRelayTransport already started");
            return;
        }
        lock (startStopClientLock)
        {
            started = true;
            try
            {
                tcpClient = new TcpClient(IpAddress, Port);
            }
            catch
            {
                return;
            }
            sendThreadRunning = true;
            sendThread = new Thread(sendThreadFunc);
            sendThread.IsBackground = true;
            sendThread.Start();

            receiveThread = new Thread(receiveThreadFunc);
            receiveThread.IsBackground = true;
            receiveThread.Start();
        }
    }

    private void StopSocket()
    {
        lock (startStopClientLock)
        {
            if (!started)
                return;
            started = false;
            sendThreadRunning = false;
            if (tcpClient != null)
            {
                tcpClient.Close();
                tcpClient.Dispose();
                tcpClient = null;
            }
        }
    }

    public void StartClient()
    {
        lock (receivedPacketScripts)
        {
            receivedPacketScripts.Clear();
        }
        isServer = false;
        StartSocket();
        SendStartClient(RoomIdToConnect, RoomPassword);
        
    }

    public void StartServer()
    {
        ClientId = 0;
        lock (receivedPacketScripts)
        {
            receivedPacketScripts.Clear();
        }

        isServer = true;
        StartSocket();
        SendStartHost(RoomPassword, RoomNameToCreate, GameName);
    }

    private void SendSubCmdClientConnected(int targetClient,int connectedClient)
    {
        using (MemoryStream ms = new MemoryStream())
        {
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                bw.Write((byte)CommandType.SendToClient);
                bw.Write(targetClient);
                bw.Write((byte)transferChannel);
                bw.Write((byte)transferMode);
                bw.Write((byte)SubCommandType.ClientConnected);
                bw.Write(connectedClient);
                SendPacket(ms.ToArray());
            }
        }
    }

    private void SendSubCmdClientDisconnected(int targetClient, int connectedClient)
    {
        using (MemoryStream ms = new MemoryStream())
        {
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                bw.Write((byte)CommandType.SendToClient);
                bw.Write(targetClient);
                bw.Write((byte)transferChannel);
                bw.Write((byte)transferMode);
                bw.Write((byte)SubCommandType.ClientDisconnected);
                bw.Write(connectedClient);
                SendPacket(ms.ToArray());
            }
        }
    }

    private void SendStartClient(string roomId, string password)
    {
        using (MemoryStream ms = new MemoryStream())
        {
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                bw.Write((byte)CommandType.StartClient);
                bw.Write(roomId);
                bw.Write(password);
                SendPacket(ms.ToArray());
            }
        }
    }

    private void SendStartHost(string password, string name, string gameName)
    {
        using (MemoryStream ms = new MemoryStream())
        {
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                bw.Write((byte)CommandType.StartHost);
                bw.Write(password);
                bw.Write(name);
                bw.Write(gameName);
                SendPacket(ms.ToArray());
            }
        }
    }

    private void HandleStartClientResponse(int clientId, int hostClientId)
    {
        GD.Print("HandleStartClientResponse clientId = " + clientId + " hostClientId = " + hostClientId);
        ClientId = clientId;
        EmitSignal("peer_connected", 1);
    }

    private void HandleStartHostResponse(int clientId, int roomId)
    {
        ClientId = clientId;
    }

    private void HandleReceiveFromClient(int fromClientId, byte channelId,TransferModeEnum packetTransferMode ,byte[] payload)
    {
        PacketScriptFromClient packetScriptFromClient = new PacketScriptFromClient();
        //Debug.Log("Received payload from client " + fromClientId + " bytes: " + payload.Count+" channel: "+re.ChannelName);
        //Debug.Log("payload-data: " + BitConverter.ToString(payload.Array, payload.Offset, payload.Count));

        SubCommandType subCmd = (SubCommandType)payload[0];
        switch (subCmd)
        {
            case SubCommandType.Payload:
                {
                    packetScriptFromClient.clientId = fromClientId;
                    packetScriptFromClient.channel = channelId;
                    packetScriptFromClient.transferMode = packetTransferMode;
                    byte[] subPayload = new byte[payload.Length-1];
                    Array.Copy(payload,1,subPayload,0,payload.Length-1);
                    packetScriptFromClient.data = subPayload;

                    lock (receivedPacketScripts)
                    {
                        receivedPacketScripts.Enqueue(packetScriptFromClient);
                    }
                }
                break;
                case SubCommandType.ClientConnected:
                {
                    int clientId=BitConverter.ToInt32(payload,1);
                    if (connectedClients.Add(clientId))
                    {
                        EmitSignal("peer_connected", clientId + 1);
                    }
                }break;
                case SubCommandType.ClientDisconnected:
                {
                    int clientId = BitConverter.ToInt32(payload, 1);
                    if (connectedClients.Contains(clientId))
                    {
                        EmitSignal("peer_disconnected", clientId + 1);
                    }
                }
                break;
        }

        
    }

    private void HandleClientDisconnected(int remoteClientId)
    {
        GD.Print("HandleClientDisconnected remoteClientId = " + remoteClientId);
        EmitSignal("peer_disconnected", remoteClientId + 1);
        foreach (int clientToNotify in connectedClients)
        {
            SendSubCmdClientDisconnected(clientToNotify, remoteClientId);
        }
        if(connectedClients.Contains(remoteClientId))
        {
            connectedClients.Remove(remoteClientId);
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

    //Only called on server
    private void HandleClientConnected(int clientId)
    {
        GD.Print("HandleClientConnected remoteClientId = " + clientId);
        EmitSignal("peer_connected", clientId + 1);
        foreach(int clientToNotify in connectedClients)
        {
            SendSubCmdClientConnected(clientToNotify,clientId);
            SendSubCmdClientConnected(clientId, clientToNotify);
        }
        connectedClients.Add(clientId);
    }

    private void HandleListRoomsResponse(RoomInfo[] roomInfos)
    {
        GD.Print("HandleListRooms response called");
        listRoomsAnswer = roomInfos;
    }

    private void HandlePacketData(byte[] data)
    {
        CommandType cmdType = (CommandType)data[4];
        if (cmdType != CommandType.Ping &&
            cmdType != CommandType.ReceiveFromClient)
        {
            GD.Print("Received command " + cmdType.ToString() + " from relay\n" +
            "Data: " + BitConverter.ToString(data));
        }
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
                    TransferModeEnum packetTransferMode = (TransferModeEnum)data[7];
                    byte[] payload = new byte[data.Length - 11];
                    Array.Copy(data, 11, payload, 0, payload.Length);
                    HandleReceiveFromClient(remoteClientId, channelId,packetTransferMode, payload);
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
                }
                break;
            case CommandType.ListRoomsResponse:
                {
                    int roomIdCount = BitConverter.ToInt32(data, 5);
                    using (MemoryStream ms = new MemoryStream(data))
                    {
                        ms.Seek(9, SeekOrigin.Begin);
                        using (BinaryReader br = new BinaryReader(ms))
                        {
                            RoomInfo[] roomInfos = new RoomInfo[roomIdCount];
                            for (int i = 0; i < roomIdCount; i++)
                            {
                                roomInfos[i] = new RoomInfo()
                                {
                                    RoomId = br.ReadString(),
                                    ClientCount = br.ReadInt32(),
                                    HasPassword = br.ReadBoolean(),
                                    Name = br.ReadString()
                                };
                            }
                            HandleListRoomsResponse(roomInfos);
                        }
                    }

                }
                break;
            default:
                {
                    GD.Print("But no function exists for this type of message");
                }
                break;
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
            GD.PrintErr("Exception in sendThread loop: " + ex.ToString());
        }
        HandleDisconnect();
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
                            GD.PrintErr("Exception while handling packet in listener thread: " + ex.ToString());
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
            GD.PrintErr("Exception in receiveThread loop: " + ex.ToString());

            //StopSocket();
        }
        HandleDisconnect();
    }

    private void HandleDisconnect()
    {
        GD.PrintErr("HandleDisconnect ClientId = " + ClientId);

        lock (startStopClientLock)
        {
            if (started)
            {
                started = false;
                tcpClient.Dispose();
                tcpClient = null;

            }
        }

        if (ClientId != -1)
        {
            //TODO: emit signal?
            ClientId = -1;
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

    private void SendListRooms(string gameName)
    {
        using (MemoryStream ms = new MemoryStream())
        {
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                bw.Write((byte)CommandType.ListRooms);
                bw.Write(gameName);
                SendPacket(ms.ToArray());
            }
        }
    }

    public void ListRooms(Action<RoomInfo[]> listRoomsCallback)
    {
        GD.Print("ListRooms called");
        StartSocket();
        currentListRoomsCallback = listRoomsCallback;
        SendListRooms(GameName);
    }

    //
    // Summary:
    //     Called when the multiplayer peer should be immediately closed (see Godot.MultiplayerPeer.Close).
    public override void _Close()
    {
        StopSocket();
    }

    //
    // Summary:
    //     Called when the connected pPeer should be forcibly disconnected (see Godot.MultiplayerPeer.DisconnectPeer(System.Int32,System.Boolean)).
    public override void _DisconnectPeer(int pPeer, bool pForce)
    {
        SendDisconnectRemoteClient(pPeer);
    }

    //
    // Summary:
    //     Called when the available packet count is internally requested by the Godot.MultiplayerApi.
    public override int _GetAvailablePacketCount()
    {
        int count = 0;
        lock(receivedPacketScripts)
        {
            count = receivedPacketScripts.Count;
        }
        return count;
    }

    //
    // Summary:
    //     Called when the connection status is requested on the Godot.MultiplayerPeer (see
    //     Godot.MultiplayerPeer.GetConnectionStatus).
    public override ConnectionStatus _GetConnectionStatus()
    {
        return  started? ConnectionStatus.Connected : ConnectionStatus.Disconnected;
    }

    //
    // Summary:
    //     Called when the maximum allowed packet size (in bytes) is requested by the Godot.MultiplayerApi.
    public override int _GetMaxPacketSize()
    {
        return int.MaxValue-1024;
    }

    //
    // Summary:
    //     Called when the ID of the Godot.MultiplayerPeer who sent the most recent packet
    //     is requested (see Godot.MultiplayerPeer.GetPacketPeer).
    public override int _GetPacketPeer()
    {
        lock(receivedPacketScripts) 
        {
            if(receivedPacketScripts.Count >= 0)
            {
                return receivedPacketScripts.Peek().clientId+1;
            }
        }
        return 0;
    }

    //
    // Summary:
    //     Called when a packet needs to be received by the Godot.MultiplayerApi, if _get_packet
    //     isn't implemented. Use this when extending this class via GDScript.
    public override byte[] _GetPacketScript()
    {
        lock(receivedPacketScripts)
        {
            if(receivedPacketScripts.Count >= 0)
            {
                return receivedPacketScripts.Dequeue().data;
            }
            else
            {
                return new byte[0];
            }
        }
    }

    //
    // Summary:
    //     Called when the transfer channel to use is read on this Godot.MultiplayerPeer
    //     (see Godot.MultiplayerPeer.TransferChannel).
    public override int _GetTransferChannel()
    {
        return transferChannel;
    }

    //
    // Summary:
    //     Called when the transfer mode to use is read on this Godot.MultiplayerPeer (see
    //     Godot.MultiplayerPeer.TransferMode).
    public override TransferModeEnum _GetTransferMode()
    {
        return transferMode;
    }

    //
    // Summary:
    //     Called when the unique ID of this Godot.MultiplayerPeer is requested (see Godot.MultiplayerPeer.GetUniqueId).
    public override int _GetUniqueId()
    {
        return ClientId+1;
    }

    //
    // Summary:
    //     Called when the "refuse new connections" status is requested on this Godot.MultiplayerPeer
    //     (see Godot.MultiplayerPeer.RefuseNewConnections).
    public override bool _IsRefusingNewConnections()
    {
        return false;
    }

    //
    // Summary:
    //     Called when the "is server" status is requested on the Godot.MultiplayerApi.
    //     See Godot.MultiplayerApi.IsServer.
    public override bool _IsServer()
    {
        return isServer;
    }

    //
    // Summary:
    //     Called when the Godot.MultiplayerApi is polled. See Godot.MultiplayerApi.Poll.
    public override void _Poll()
    {
        if (listRoomsAnswer != null && currentListRoomsCallback != null)
        {
            currentListRoomsCallback.Invoke(listRoomsAnswer);
            listRoomsAnswer = null;
            currentListRoomsCallback = null;
        }
    }

    //
    // Summary:
    //     Called when a packet needs to be sent by the Godot.MultiplayerApi, if _put_packet
    //     isn't implemented. Use this when extending this class via GDScript.
    public override Error _PutPacketScript(byte[] pBuffer)
    {
        //GD.Print("PutPacket called targetPeer=" + targetPeer);
        using (MemoryStream ms = new MemoryStream())
        {
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                bw.Write((byte)CommandType.SendToClient);
                bw.Write(targetPeer);
                bw.Write((byte)transferChannel);
                bw.Write((byte)transferMode);
                bw.Write((byte)SubCommandType.Payload);
                bw.Write(pBuffer, 0, pBuffer.Length);
                SendPacket(ms.ToArray());
            }
        }
        return Error.Ok;
    }

    //
    // Summary:
    //     Called when the "refuse new connections" status is set on this Godot.MultiplayerPeer
    //     (see Godot.MultiplayerPeer.RefuseNewConnections).
    public override void _SetRefuseNewConnections(bool pEnable)
    {
        refuseNewConnections = pEnable;
    }

    //
    // Summary:
    //     Called when the target peer to use is set for this Godot.MultiplayerPeer (see
    //     Godot.MultiplayerPeer.SetTargetPeer(System.Int32)).
    public override void _SetTargetPeer(int pPeer)
    {
        //if(pPeer <= 0)
        {
            //GD.Print("_SetTargetPeer:  " + pPeer);
        }
        targetPeer = pPeer-1;
    }

    //
    // Summary:
    //     Called when the channel to use is set for this Godot.MultiplayerPeer (see Godot.MultiplayerPeer.TransferChannel).
    public override void _SetTransferChannel(int pChannel)
    {
        transferChannel = pChannel;
    }

    //
    // Summary:
    //     Called when the transfer mode is set on this Godot.MultiplayerPeer (see Godot.MultiplayerPeer.TransferMode).
    public override void _SetTransferMode(TransferModeEnum pMode)
    {
        transferMode = pMode;
    }

    public virtual int _get_packet_channel()
    {
        lock (receivedPacketScripts)
        {
            if (receivedPacketScripts.Count >= 0)
            {
                return receivedPacketScripts.Peek().channel;
            }
        }
        return 0;
    }

    public virtual int _get_packet_mode()
    {
        lock (receivedPacketScripts)
        {
            if (receivedPacketScripts.Count >= 0)
            {
                return (int)receivedPacketScripts.Peek().transferMode;
            }
        }
        return (int)TransferModeEnum.Reliable;
    }

    public virtual bool is_server_relay_supported()
    {
        return true;
    }

}
