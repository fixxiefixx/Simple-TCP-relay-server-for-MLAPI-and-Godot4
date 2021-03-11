using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Text;

namespace SimpleTcpRelay
{
    public class RoomManager
    {
        private const int MaxRoomId = 1000;

        private static int AtRoomId = 0;

        

        public class Room
        {
            private int atClientId = 0;

            public int RoomId = -1;
            public int HostClientId = -1;
            public Dictionary<int, RelayClient> clients = new Dictionary<int, RelayClient>();

            public int GetNextClientId()
            {
                return atClientId++;
            }
        }

        private Dictionary<int, Room> rooms = new Dictionary<int, Room>();

        private int GetNextRoomId()
        {
            int roomId = AtRoomId++;
            if (AtRoomId > MaxRoomId)
                AtRoomId = 0;
            return roomId;
        }

        public Room GetRoom(int roomId)
        {
            return rooms[roomId];
        }

        public int CreateRoom(out int clientId,RelayClient client)
        {
            int roomId = GetNextRoomId();
            Room room = new Room()
            {
                RoomId = roomId
            };
            clientId = room.GetNextClientId();
            room.clients.Add(clientId, client);
            room.HostClientId = clientId;
            rooms.Add(roomId, room);
            return roomId;
        }

        public void DestroyRoom(int roomId)
        {
            Room room = rooms[roomId];
            foreach(RelayClient client in room.clients.Values)
            {
                if (client.IsStarted)
                {
                    //TODO: Send disconnect packet to client
                    client.Stop();
                }
            }
            room.clients.Clear();
        }

        public int AddClientToRoom(RelayClient client,int roomId)
        {
            Room room = rooms[roomId];
            int clientId = room.GetNextClientId();
            room.clients.Add(clientId, client);
            return clientId;
        }
    }
}
