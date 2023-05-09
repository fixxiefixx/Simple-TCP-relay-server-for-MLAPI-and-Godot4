# Simple-TCP-relay-server-for-MLAPI
A simple TCP based relay server and transport for the Unity MLAPI networking library

This relay server was tested with the MLAPI version 12.1.7 and Godot 4.0.2 (Mono version).

# How to use
You need to install .Net Core 2.0 to run the relay.

Compile the project with Visual Studio 2019

Start the relay server using following pattern
```cmd
dotnet SimpleTcpRelay.dll [ListenIpAddress] [port]
```
This example starts the relay server on the Ip "127.0.0.1" and port "1234":

```cmd
dotnet SimpleTcpRelay.dll 127.0.0.1 1234
```

# For MLAPI in Unity
Copy the script "SimpleTcpRelayTransport.cs" from the "Transports/Unity_MLAPI" folder into the Assets folder of your Unity project.

Import the MLAPI Asset from the Asset store.

Follow an MLAPI tutorial how to create the NetworkingManager object in the Unity scene.

Add the "SimpleTcpRelayTransport" script to the scene object containing the NetworkingManager.

In the NetworkingManager inspector set the NetworkTransport reference to the SimpleTcpRelayTransport script.

Configure the Ip Address and port settings of the "SimpleTcpRelayTransport" in the inspector so that it can connect to the relay server. 

When you start the NetworkingManager in host mode you can get the connected RoomId with following line:
```csharp
int roomId = NetworkingManager.Singleton.GetComponent<SimpleTcpRelayTransport>().ConnectedRoomId;
```
the RoomId can be set in the inspector of the SimpleTcpRelayTransport script or by assigning a value to the public RoomIdToConnect variable:
```csharp
NetworkingManager.Singleton.GetComponent<SimpleTcpRelayTransport>().RoomIdToConnect = value;
```
The RoomId is required when you start the NetworkingManager as client.

The first room created has always the RoomId "0".

# For Godot4
Copy the script "SimpleRelayMultiplayerPeer.cs" into your Godot4 project.

You can use the following code to start a relay multiplayer session:

```C#
SimpleRelayMultiplayerPeer peer = new SimpleRelayMultiplayerPeer();
peer.IpAddress = "127.0.0.1"; //Here you can enter the Ip of your self hosted relay server
peer.Port = 8765; //This is the standard port of the relay server
peer.GameName = "ExampleGame"; //Here you can enter the name of your game. Only Clients that use this name can see your session.
peer.StartServer();
GetTree().GetMultiplayer().MultiplayerPeer = peer;
```

Use the following code to list available game sessions on the relay server:

```C#
SimpleRelayMultiplayerPeer mp = new SimpleRelayMultiplayerPeer();
mp.IpAddress = "127.0.0.1"; //Here you can enter the Ip of your self hosted relay server
mp.Port = 8765; //This is the standard port of the relay server
mp.GameName = "ExampleGame"; //Here you can enter the name of your game. Only Clients that use this name can see your session.
string roomId = null;
mp.ListRooms(
       delegate (SimpleRelayMultiplayerPeer.RoomInfo[] roomInfos)
       {
           //Here you can do something with the rooms.
           //Maybe show a list with the rooms to the player so he can select a room to connect
       });
```

Use the following code to connect to a room as a client:

```C#
SimpleRelayMultiplayerPeer peer = new SimpleRelayMultiplayerPeer();
peer.IpAddress = "127.0.0.1"; //Here you can enter the Ip of your self hosted relay server
peer.Port = 8765; //This is the standard port of the relay server
peer.GameName = "ExampleGame"; //Here you can enter the name of your game. Only Clients that use this name can see your session.
peer.RoomIdToConnect = "RoomIdHere";//Here you can assign the roomid from the room the client will connect to.
peer.StartClient();
Multiplayer.MultiplayerPeer = peer;
```
