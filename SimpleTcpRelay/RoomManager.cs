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

        private Queue<int> reusableRoomIds = new Queue<int>();

        

        public class Room
        {
            private int atClientId = 0;

            public int RoomId = -1;
            public int HostClientId = -1;
            public Dictionary<int, RelayClient> clients = new Dictionary<int, RelayClient>();

            public int GetNextClientId()
            {
                int tmp = atClientId++;
                return tmp;
            }
        }

        private Dictionary<int, Room> rooms = new Dictionary<int, Room>();

        private int GetNextRoomId()
        {
            if (reusableRoomIds.Count > 0)
                return reusableRoomIds.Dequeue();

            int roomId = AtRoomId++;
            if (AtRoomId > MaxRoomId)
                AtRoomId = 0;
            return roomId;
        }

        public Room[] GetRooms()
        {
            List<Room> roomsToReturn = new List<Room>();
            foreach(Room room in rooms.Values)
            {
                roomsToReturn.Add(room);
            }
            return roomsToReturn.ToArray();
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
            Console.WriteLine("Created room " + roomId + " for client " + clientId);
            return roomId;
        }

        public void DestroyRoom(int roomId)
        {
            Console.WriteLine("Destroying room "+roomId);
            Room room = rooms[roomId];
            room.clients.Clear();
            rooms.Remove(roomId);
            reusableRoomIds.Enqueue(roomId);
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
