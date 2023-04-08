using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Text;

namespace SimpleTcpRelay
{
    public class RoomManager
    {
        public class Room
        {
            private int atClientId = 0;

            public string RoomId = null;
            public string GameName = null;
            public int HostClientId = -1;
            public string Password = "";
            public string Name = "";
            public bool Visible = true;

            public Dictionary<int, RelayClient> clients = new Dictionary<int, RelayClient>();

            public int GetNextClientId()
            {
                int tmp = atClientId++;
                return tmp;
            }
        }

        private Dictionary<string, Room> rooms = new Dictionary<string, Room>();

        private string GetNextRoomId()
        {

            return Guid.NewGuid().ToString();
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

        public Room GetRoom(string roomId)
        {
            return rooms[roomId];
        }

        public string CreateRoom(out int clientId,RelayClient client, string password, string name, string gameName)
        {
            
            string roomId = GetNextRoomId();
            
            Room room = new Room()
            {
                RoomId = roomId,
                Password = password,
                Name = name,
                GameName = gameName
            };
            clientId = room.GetNextClientId();
            room.clients.Add(clientId, client);
            room.HostClientId = clientId;
            rooms.Add(roomId, room);
            Console.WriteLine("Created room " + roomId + " for client " + clientId);
            return roomId;
        }

        public void DestroyRoom(string roomId)
        {
            Console.WriteLine("Destroying room "+roomId);
            Room room = rooms[roomId];
            room.clients.Clear();
            rooms.Remove(roomId);
        }

        public int AddClientToRoom(RelayClient client,string roomId)
        {
            Room room = rooms[roomId];
            int clientId = room.GetNextClientId();
            room.clients.Add(clientId, client);
            return clientId;
        }
    }
}
