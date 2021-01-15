﻿using System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Bomberman.Client.ServerSide
{
    public static class PacketHandler
    {
        private const int MaxPacketSize = 1024;

        public static async Task SendPacket(TcpClient client, Packet packet)
        {
            try
            {
                // convert JSON to buffer and its length to a 16 bit unsigned integer buffer
                byte[] jsonBuffer = packet != null ? PacketProtocol.WrapMessage(Encoding.UTF8.GetBytes(packet.ToJson())) : PacketProtocol.WrapKeepaliveMessage();

                // Send the packet
                await client.GetStream().WriteAsync(jsonBuffer, 0, jsonBuffer.Length);
            }
            catch (Exception e)
            {
                Console.WriteLine("There was an issue receiving a packet.");
                Console.WriteLine("Reason: {0}", e.Message);
                throw;
            }
        }

        public static async Task ReceivePackets(TcpClient client, Action<TcpClient, Packet> action)
        {
            try
            {
                // First check there is data available
                if (client.Available == 0)
                    return;

                var packetProtocol = new PacketProtocol(MaxPacketSize);
                packetProtocol.MessageArrived += (data) =>
                {
                    if (data.Length == 0)
                    {
                        return;
                    }

                    // Convert data into a packet datatype
                    string jsonString = Encoding.UTF8.GetString(data);
                    var packet = Packet.FromJson(jsonString);
                    action(client, packet);
                };

                // Read data through protocol
                var stream = client.GetStream();
                while (stream.DataAvailable && stream.CanRead)
                {
                    var readBuffer = new byte[MaxPacketSize];
                    await stream.ReadAsync(readBuffer);
                    packetProtocol.DataReceived(readBuffer);
                }
            }
            catch (Exception e)
            {
                // There was an issue in receiving
                Console.WriteLine("There was an issue sending a packet to {0}.", client.Client.RemoteEndPoint);
                Console.WriteLine("Reason: {0}", e.Message);
                throw;
            }
        }
    }
}
