﻿using Bomberman.Client.ServerSide;
using Microsoft.Xna.Framework;
using Server.GameLogic;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Server
{
    public class Network
    {
        public static Network Instance { get; private set; }

        private const float HeartbeatInterval = 5f;
        private const int GameStartCountdownSeconds = 30;

        private readonly string _serverIp;
        private readonly int _serverPort;
        private readonly TcpListener _listener;
        public readonly Dictionary<TcpClient, string> Clients;
        private Dictionary<TcpClient, PlayerContext> _players;
        public readonly Dictionary<TcpClient, bool> WaitingLobby;
        private readonly int _maxPlayers;

        private readonly System.Timers.Timer _heartbeatTimer;
        private bool _running;
        private GameLogic.Game _game;

        private readonly System.Timers.Timer _gameStartTimer;

        private readonly Dictionary<TcpClient, HeartbeatCheck> _timeSinceLastHeartbeat;

        public bool GameOngoing { get { return _game != null; } }

        private class HeartbeatCheck
        {
            public DateTime Time { get; private set; }
            public bool HasBeenSend { get; private set; }

            private readonly TcpClient _client;

            public HeartbeatCheck(TcpClient client)
            {
                _client = client;
                Reset();
            }

            public void Send()
            {
                HasBeenSend = true;
                Time = DateTime.Now;
                SendHeartbeat(_client);
            }

            public void Reset()
            {
                HasBeenSend = false;
                Time = DateTime.Now;
            }

            private void SendHeartbeat(TcpClient client)
            {
                Instance.SendPacket(client, new Packet("heartbeat"));
            }
        }

        public string GetClientPlayerName(TcpClient client)
        {
            if (Clients.TryGetValue(client, out string playerName))
                return playerName;
            return null;
        }

        public async void SendPacket(TcpClient client, Packet packet)
        {
            try
            {
                if (!client.Connected) return;
                await PacketHandler.SendPacket(client, packet, true);
            }
            catch (ObjectDisposedException)
            {
                HandleDisconnectedClient(client);
            }
            catch (SocketException e)
            {
                Console.WriteLine("SocketException: " + e.ToString());
                HandleDisconnectedClient(client);
            }
            catch (IOException e)
            {
                Console.WriteLine("IOException: " + e.ToString());
                HandleDisconnectedClient(client);
            }
        }

        public Network(string serverIp, int serverPort, int maxPlayers)
        {
            Instance = this;

            _serverIp = serverIp;
            _serverPort = serverPort;
            _maxPlayers = maxPlayers;
            _running = false;
            Clients = new Dictionary<TcpClient, string>();
            _timeSinceLastHeartbeat = new Dictionary<TcpClient, HeartbeatCheck>();
            _listener = new TcpListener(IPAddress.Any, serverPort);
            WaitingLobby = new Dictionary<TcpClient, bool>();

            // Countdown timer for game start once 2 or more players are ready
            _gameStartTimer = new System.Timers.Timer(1000)
            {
                AutoReset = true
            };
            _gameStartTimer.Elapsed += GameStartTimer_Elapsed;

            _heartbeatTimer = new System.Timers.Timer(1000);
            _heartbeatTimer.Elapsed += HeartbeatTimer_Elapsed;
        }

        private int _currentCountdown = GameStartCountdownSeconds;
        private bool _isCountingDown = false;
        private void GameStartTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (!_isCountingDown)
            {
                _gameStartTimer.Stop();
                return;
            }

            _currentCountdown--;

            // Let clients know timer has started
            foreach (var p in WaitingLobby.Where(a => a.Value))
            {
                SendPacket(p.Key, new Packet("gamecountdown", _currentCountdown.ToString()));
            }

            if (_currentCountdown == 0)
            {
                // Reset
                _currentCountdown = GameStartCountdownSeconds;
                _gameStartTimer.Stop();
                StartGame();
            }
        }

        private void StartGame()
        {
            var readyClients = WaitingLobby
                .Where(a => a.Value)
                .Select(a => a.Key)
                .ToList();
            var clients = Clients
                .Where(a => readyClients.Contains(a.Key))
                .ToDictionary(a => a.Key, a => a.Value);
            _game = new GameLogic.Game(clients, out _players);
        }

        private readonly List<TcpClient> _clientsToRemoveFromHeartbeatMonitoring = new List<TcpClient>();
        private void HeartbeatTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (!_running)
            {
                _heartbeatTimer.Stop();
                return;
            }

            foreach (var client in _timeSinceLastHeartbeat)
            {
                if (!Clients.ContainsKey(client.Key)) continue;
                var heartbeatCheck = _timeSinceLastHeartbeat[client.Key];
                if (heartbeatCheck.Time.AddMilliseconds(HeartbeatInterval * 1000) <= DateTime.Now)
                {
                    if (!heartbeatCheck.HasBeenSend)
                        heartbeatCheck.Send();
                    else
                    {
                        Console.WriteLine("Client [" + client.Key.Client.RemoteEndPoint + "] did not respond to heartbeat check in time, closing down client resources.");
                        _clientsToRemoveFromHeartbeatMonitoring.Add(client.Key);
                    }
                }
            }

            foreach (var client in _clientsToRemoveFromHeartbeatMonitoring)
            {
                HandleDisconnectedClient(client);
            }
            _clientsToRemoveFromHeartbeatMonitoring.Clear();
        }

        public void Shutdown()
        {
            if (_running)
            {
                _running = false;
                Console.WriteLine("Shutting down server..");
            }
        }

        public void Run()
        {
            Console.WriteLine($"Starting server on {_serverIp}:{_serverPort}.");
            Console.WriteLine("Press Ctrl-C to shutdown the server at any time.");

            _listener.Start();
            _running = true;

            Console.WriteLine($"Server is online and waiting for incoming connections.");

            // Start heartbeat timer
            _heartbeatTimer.Start();

            var tasks = new List<Task>();
            while (_running)
            {
                tasks.RemoveAll(a => a.IsCompleted);

                if (_listener.Pending())
                {
                    tasks.Add(HandleNewConnection());
                }

                // Check game logic steps
                foreach (var client in Clients.ToList())
                {
                    try
                    {
                        tasks.Add(PacketHandler.ReceivePackets(client.Key, HandlePacket, true));
                    }
                    catch (SocketException)
                    {
                        HandleDisconnectedClient(client.Key);
                    }
                    catch (IOException)
                    {
                        HandleDisconnectedClient(client.Key);
                    }
                }
            }

            // Allow tasks to finish
            Task.WaitAll(tasks.ToArray(), 1000);

            // Disconnect any clients still here
            Parallel.ForEach(Clients, (client) =>
            {
                DisconnectClient(client.Key, "The server is being shutdown.");
            });

            // Cleanup our resources
            _listener.Stop();

            // Info
            Console.WriteLine("The server has been shut down.");
        }

        private void HandlePacket(TcpClient client, Packet packet)
        {
            _timeSinceLastHeartbeat[client].Reset();
            if (packet == null) return;

            if (!Packet.ReadableOpCodes.TryGetValue(packet.OpCode, out string readableOpCode))
            {
                Console.WriteLine("Unhandled packet: " + packet.ToString());
                return;
            }

            try
            {
                switch (readableOpCode)
                {
                    case "heartbeat":
                        // Automatically handled
                        return;
                    case "moveleft":
                        if (_game == null) return;
                        _game.Move(client, new Point(-1, 0), readableOpCode);
                        break;
                    case "moveright":
                        if (_game == null) return;
                        _game.Move(client, new Point(1, 0), readableOpCode);
                        break;
                    case "moveup":
                        if (_game == null) return;
                        _game.Move(client, new Point(0, -1), readableOpCode);
                        break;
                    case "movedown":
                        if (_game == null) return;
                        _game.Move(client, new Point(0, 1), readableOpCode);
                        break;
                    case "placebomb":
                        if (_game == null) return;
                        _game.PlaceBomb(client);
                        break;
                    case "bye":
                        HandleDisconnectedClient(client);
                        break;
                    case "playername":
                        string playerName = packet.Arguments;

                        // Sanity check for name hacks
                        if (string.IsNullOrWhiteSpace(playerName) || playerName.Length > 10)
                        {
                            DisconnectClient(client, $"Name: [{playerName}] is too long, must be within 10 characters.");
                            return;
                        }

                        // Sanity check if name already exists
                        if (Clients.Any(a => a.Value != null && a.Value.Equals(playerName, StringComparison.OrdinalIgnoreCase)))
                        {
                            DisconnectClient(client, $"Name: [{playerName}] is already taken.");
                            return;
                        }

                        if (Clients.ContainsKey(client))
                            Clients[client] = playerName;

                        WaitingLobby.Add(client, false);

                        // Add all clients in waiting lobby to the client's lobby, including himself
                        foreach (var c in WaitingLobby)
                            SendPacket(client, new Packet("joinwaitinglobby", Clients[c.Key]));

                        // Tell other clients that we joined the waiting lobby
                        foreach (var c in WaitingLobby.Where(a => a.Key != client))
                        {
                            SendPacket(c.Key, new Packet("joinwaitinglobby", playerName));
                        }
                        break;
                    case "ready":
                        if (!WaitingLobby.ContainsKey(client))
                        {
                            Console.WriteLine("Received a ready response but client was not in waiting lobby.");
                            return;
                        }

                        var ready = packet.Arguments.Equals("1");
                        WaitingLobby[client] = ready;

                        // If a game is already ongoing, we don't need to overwrite the current with new players
                        if (GameOngoing)
                        {
                            SendPacket(client, new Packet("message", "A game is already ongoing, please wait!"));
                            return;
                        }

                        // Check if more than one client is ready then start the countdown
                        // Else stop the countdown and reset it
                        // If all clients are ready, start game instantly.
                        var amountReady = WaitingLobby.Where(a => a.Value).ToList();
                        if (WaitingLobby.Count != 0 && amountReady.Count == WaitingLobby.Count)
                        {
                            _isCountingDown = false;
                            _gameStartTimer.Stop();
                            _currentCountdown = GameStartCountdownSeconds;
                            StartGame();
                        }
                        else if (amountReady.Count >= 2)
                        {
                            // Let clients know timer has started
                            foreach (var p in amountReady)
                            {
                                SendPacket(p.Key, new Packet("gamecountdown", _currentCountdown.ToString()));
                            }

                            _isCountingDown = true;
                            _gameStartTimer.Start();
                        }
                        else
                        {
                            _isCountingDown = false;
                            _gameStartTimer.Stop();
                            _currentCountdown = GameStartCountdownSeconds;

                            // Notify everyone that countdown stopped
                            foreach (var p in WaitingLobby)
                            {
                                SendPacket(p.Key, new Packet("gamecountdown", "0"));
                            }
                        }

                        // Inform client to stop countdown if hes not ready, but >= 2 are
                        if (_isCountingDown && !ready)
                        {
                            SendPacket(client, new Packet("gamecountdown", "0"));
                        }

                        // Let clients know the client has readied/unreadied
                        foreach (var c in WaitingLobby.Where(a => a.Key != client))
                        {
                            SendPacket(c.Key, new Packet(ready ? "ready" : "unready", Clients[client]));
                        }
                        break;
                    default:
                        Console.WriteLine("Unhandled packet: " + packet.ToString());
                        break;
                }
            }
            catch(SocketException)
            {
                HandleDisconnectedClient(client);
            }
            catch(IOException)
            {
                HandleDisconnectedClient(client);
            }
        }

        public void ResetGame()
        {
            foreach (var player in _game.Players)
            {
                // Send gameover packet
                SendPacket(player.Key, new Packet("gameover"));

                // Add back to waiting lobby
                WaitingLobby.Add(player.Key, false);
            }

            // Send everyone to the waiting lobby
            foreach (var player in Clients.Where(a => a.Value != null))
            {
                SendPacket(player.Key, new Packet("joinwaitinglobby", Clients[player.Key]));

                foreach (var other in Clients.Where(a => a.Value != null && a.Key != player.Key))
                {
                    SendPacket(player.Key, new Packet("joinwaitinglobby", Clients[other.Key]));
                }
            }

            // Clear for new game
            _game = null;
        }

        // Awaits for a new connection and then adds them to the waiting lobby
        private async Task HandleNewConnection()
        {
            // See if a new connection attempted to join
            TcpClient newClient = await _listener.AcceptTcpClientAsync();
            newClient.NoDelay = true;
            newClient.Client.NoDelay = true;
            newClient.ReceiveBufferSize = 250;

            Console.WriteLine("New connection from {0}.", newClient.Client.RemoteEndPoint);

            // Disconnect client because server is full.
            if (Clients.Count == _maxPlayers)
            {
                DisconnectClient(newClient, "Server is full.");
                return;
            }

            // Store them and put them in the game
            Clients.Add(newClient, null);

            // Add client
            _timeSinceLastHeartbeat.Add(newClient, new HeartbeatCheck(newClient));
        }

        // Will attempt to gracefully disconnect a TcpClient
        // This should be use for clients that may be in a game
        public void DisconnectClient(TcpClient client, string message = "")
        {
            Console.WriteLine("Disconnecting the client from {0}.", client.Client.RemoteEndPoint);

            if (message == "")
                message = "Goodbye.";

            SendPacket(client, new Packet("bye", message));

            // Let packed be processed by client
            Thread.Sleep(20);

            // Cleanup resources on our end
            HandleDisconnectedClient(client);
        }

        // Cleans up the resources if a client has disconnected,
        // gracefully or not.  Will remove them from clint list and lobby
        public void HandleDisconnectedClient(TcpClient client)
        {
            // We already handled this client, this call came from lingering packets
            if (!Clients.ContainsKey(client)) return;

            Console.WriteLine("Client disconnected.");

            // Tell everyone that this client left during the game
            if (GameOngoing && _game.Players.TryGetValue(client, out PlayerContext player))
            {
                player.Alive = false;

                // Let players know that this player left
                foreach (var c in _game.Players.Where(a => a.Key != client))
                {
                    SendPacket(c.Key, new Packet("playerdied", player.Id.ToString()));
                }

                // Check if there is 1 or no players left alive, then reset the game
                if (_game.Players.Count(a => a.Value.Alive) <= 1)
                {
                    _game.Players.Remove(client);
                    ResetGame();
                }
                else
                {
                    _game.Players.Remove(client);
                }
            }

            // First notify other waiting lobby clients if this one is still in waiting lobby
            if (WaitingLobby.ContainsKey(client))
            {
                if (!GameOngoing)
                {
                    var newLobby = WaitingLobby.Where(a => a.Key != client).ToList();
                    if (newLobby.Count != 0 && newLobby.Count(a => a.Value) == newLobby.Count)
                    {
                        _isCountingDown = false;
                        _gameStartTimer.Stop();
                        _currentCountdown = GameStartCountdownSeconds;
                        StartGame();
                    }
                    else if (newLobby.Count != 0 && newLobby.Count(a => a.Value) < 2)
                    {
                        _isCountingDown = false;
                        _gameStartTimer.Stop();
                        _currentCountdown = GameStartCountdownSeconds;

                        // Notify everyone that countdown stopped
                        foreach (var p in newLobby)
                        {
                            SendPacket(p.Key, new Packet("gamecountdown", "0"));
                        }
                    }
                }

                foreach (var c in WaitingLobby)
                {
                    if (c.Key != client)
                        SendPacket(c.Key, new Packet("removefromwaitinglobby", Clients[client]));
                }
            }

            // Remove from collections and free resources
            _timeSinceLastHeartbeat.Remove(client);
            Clients.Remove(client);
            _players?.Remove(client);
            WaitingLobby.Remove(client);
            PacketHandler.RemoveClientPacketProtocol(client);
            CleanupClient(client);
        }

        // cleans up resources for a TcpClient and closes it
        private static void CleanupClient(TcpClient client)
        {
            if (client.Connected)
                client.GetStream().Close();     // Close network stream
            client.Close();                 // Close client
        }
    }
}
