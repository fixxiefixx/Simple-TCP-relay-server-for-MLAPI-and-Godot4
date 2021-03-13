# Simple-TCP-relay-server-for-MLAPI
A simple TCP based relay server and transport for the Unity MLAPI networking library

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
Copy the script "SimpleTcpRelayTransport.cs" from the "Transports/Runtime" folder into the Assets folder of your Unity project.

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
