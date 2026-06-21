using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using LiteNetLib;
using LiteNetLib.Utils;

namespace LibSample
{
    static class NatPunchStandaloneTest
    {
        private const int ServerPort = 50033;
        private const string DefaultServerHost = "37.34.188.126";
        private const string Token = "test_room_1";
        private const string ConnectionKey = "natpunch_key";
        private static readonly TimeSpan KickTime = TimeSpan.FromMinutes(2);
        private const int FileChunkSize = 32 * 1024; // 32 KB per chunk

        // Message type prefix byte
        private const byte MSG_TEXT       = 0;
        private const byte MSG_FILE_START = 1; // filename (string), totalBytes (int), totalChunks (int)
        private const byte MSG_FILE_CHUNK = 2; // chunkIndex (int), data (bytes with length)
        private const byte MSG_FILE_END   = 3; // totalChunks (int)

        public static bool Verbose = false;

        private static void Log(string tag, string msg)
        {
            if (Verbose)
                Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}][{tag}] {msg}");
        }

        private static void Info(string msg) => Console.WriteLine(msg);

        // -------------------------------------------------------------------------
        // File receive state (per connection — single peer assumed)
        // -------------------------------------------------------------------------
        private class FileReceiveState
        {
            public string FileName;
            public int TotalBytes;
            public int TotalChunks;
            public byte[] Buffer;
            public int BytesReceived;
            public int ChunksReceived;
        }

        // -------------------------------------------------------------------------
        // Shared receive handler — returns true if caller should print [Peer] text
        // -------------------------------------------------------------------------
        private static FileReceiveState _fileReceive;

        private static void HandleReceive(NetDataReader reader, string logTag)
        {
            var type = reader.GetByte();
            switch (type)
            {
                case MSG_TEXT:
                    Console.WriteLine($"[Peer] {reader.GetString()}");
                    break;

                case MSG_FILE_START:
                    var fname     = reader.GetString();
                    var totalB    = reader.GetInt();
                    var totalC    = reader.GetInt();
                    _fileReceive  = new FileReceiveState
                    {
                        FileName      = fname,
                        TotalBytes    = totalB,
                        TotalChunks   = totalC,
                        Buffer        = new byte[totalB],
                        BytesReceived = 0,
                        ChunksReceived = 0,
                    };
                    Info($"[File] Incoming: \"{fname}\"  {totalB:#,0} bytes  {totalC} chunks");
                    Log(logTag, $"FILE_START name={fname} totalBytes={totalB} totalChunks={totalC}");
                    break;

                case MSG_FILE_CHUNK:
                    if (_fileReceive == null) { Info("[File] ERROR: chunk received but no active transfer"); break; }
                    var chunkIdx  = reader.GetInt();
                    var chunkData = reader.GetBytesWithLength();
                    int offset    = chunkIdx * FileChunkSize;
                    Array.Copy(chunkData, 0, _fileReceive.Buffer, offset, chunkData.Length);
                    _fileReceive.BytesReceived  += chunkData.Length;
                    _fileReceive.ChunksReceived += 1;
                    Log(logTag, $"FILE_CHUNK [{chunkIdx}/{_fileReceive.TotalChunks - 1}]  {chunkData.Length} bytes");
                    // simple progress every 10 chunks
                    if (_fileReceive.ChunksReceived % 10 == 0 || _fileReceive.ChunksReceived == _fileReceive.TotalChunks)
                        Info($"[File] Progress: {_fileReceive.BytesReceived:#,0}/{_fileReceive.TotalBytes:#,0} bytes");
                    break;

                case MSG_FILE_END:
                    var sentChunks = reader.GetInt();
                    if (_fileReceive == null) { Info("[File] ERROR: end received but no active transfer"); break; }
                    Log(logTag, $"FILE_END sentChunks={sentChunks} receivedChunks={_fileReceive.ChunksReceived}");
                    var savePath = Path.Combine(Directory.GetCurrentDirectory(), _fileReceive.FileName);
                    // avoid overwriting — append a counter if file exists
                    if (File.Exists(savePath))
                    {
                        var stem = Path.GetFileNameWithoutExtension(_fileReceive.FileName);
                        var ext  = Path.GetExtension(_fileReceive.FileName);
                        int n = 1;
                        while (File.Exists(savePath))
                            savePath = Path.Combine(Directory.GetCurrentDirectory(), $"{stem}({n++}){ext}");
                    }
                    File.WriteAllBytes(savePath, _fileReceive.Buffer);
                    Info($"[File] Saved: {savePath}  ({_fileReceive.BytesReceived:#,0} bytes)");
                    _fileReceive = null;
                    break;

                default:
                    Log(logTag, $"Unknown message type {type} — ignoring");
                    break;
            }
        }

        // -------------------------------------------------------------------------
        // File send helper
        // -------------------------------------------------------------------------
        private static void SendFile(string path, NetPeer peer, string logTag)
        {
            if (!File.Exists(path))
            {
                Info($"[File] Not found: {path}");
                return;
            }

            var data       = File.ReadAllBytes(path);
            var filename   = Path.GetFileName(path);
            int totalChunks = (data.Length + FileChunkSize - 1) / FileChunkSize;
            if (totalChunks == 0) totalChunks = 1;

            Info($"[File] Sending \"{filename}\"  {data.Length:#,0} bytes  {totalChunks} chunks...");
            Log(logTag, $"SendFile path={path} size={data.Length} chunks={totalChunks}");

            var w = new NetDataWriter();

            // FILE_START
            w.Reset();
            w.Put(MSG_FILE_START);
            w.Put(filename);
            w.Put(data.Length);
            w.Put(totalChunks);
            peer.Send(w, DeliveryMethod.ReliableOrdered);

            // FILE_CHUNKs
            for (int i = 0; i < totalChunks; i++)
            {
                int start     = i * FileChunkSize;
                int len       = Math.Min(FileChunkSize, data.Length - start);
                var chunk     = new byte[len];
                Array.Copy(data, start, chunk, 0, len);

                w.Reset();
                w.Put(MSG_FILE_CHUNK);
                w.Put(i);
                w.PutBytesWithLength(chunk);
                peer.Send(w, DeliveryMethod.ReliableOrdered);
                Log(logTag, $"Sent chunk [{i}/{totalChunks - 1}]  {len} bytes");
            }

            // FILE_END
            w.Reset();
            w.Put(MSG_FILE_END);
            w.Put(totalChunks);
            peer.Send(w, DeliveryMethod.ReliableOrdered);

            Info($"[File] Sent \"{filename}\" ({totalChunks} chunks).");
        }

        // =========================================================================
        // SERVER
        // =========================================================================
        public static void RunServer()
        {
            Info($"=== NAT Punch Relay Server ===  port={ServerPort}  token=\"{Token}\"");
            Log("Server", $"KickTime: {KickTime.TotalSeconds}s");

            var netListener = new EventBasedNetListener();
            netListener.ConnectionRequestEvent += req =>
            {
                Log("Server", $"Unexpected connection request from {req.RemoteEndPoint} — rejecting");
                req.Reject();
            };
            netListener.NetworkErrorEvent += (ep, err) =>
                Log("Server", $"NetworkError from {ep}: {err}");

            var server = new NetManager(netListener) { NatPunchEnabled = true, IPv6Enabled = true };
            server.Start(ServerPort);
            Log("Server", $"UDP socket started. LocalPort={server.LocalPort}");

            var waitingPeers = new Dictionary<string, WaitPeer>();
            var peersToRemove = new List<string>();

            var natListener = new EventBasedNatPunchListener();
            natListener.NatIntroductionRequest += (localEndPoint, remoteEndPoint, token) =>
            {
                Log("Server", $"NatIntroduceRequest  token={token}  internal={localEndPoint}  external={remoteEndPoint}");

                if (waitingPeers.TryGetValue(token, out var wpeer))
                {
                    Log("Server", $"  Waiting peer exists  stored_internal={wpeer.InternalAddr}  stored_external={wpeer.ExternalAddr}");

                    if (wpeer.ExternalAddr.Equals(remoteEndPoint))
                    {
                        Log("Server", "  Same peer refresh (external match) — refreshing timestamp");
                        wpeer.Refresh();
                        return;
                    }

                    Log("Server", $"  Second peer arrived — introducing");
                    Log("Server", $"  PeerA: internal={wpeer.InternalAddr}  external={wpeer.ExternalAddr}");
                    Log("Server", $"  PeerB: internal={localEndPoint}  external={remoteEndPoint}");
                    server.NatPunchModule.NatIntroduce(
                        wpeer.InternalAddr, wpeer.ExternalAddr,
                        localEndPoint,      remoteEndPoint,
                        token);
                    Info($"[Server] Introduced peers for token={token}");
                    waitingPeers.Remove(token);
                }
                else
                {
                    Info($"[Server] First peer waiting  token={token}  external={remoteEndPoint}");
                    Log("Server", $"  internal={localEndPoint}");
                    waitingPeers[token] = new WaitPeer(localEndPoint, remoteEndPoint);
                    Log("Server", $"  WaitingPeers count: {waitingPeers.Count}");
                }
            };

            server.NatPunchModule.Init(natListener);

            Info("Press ESC to stop.");
            int tickCount = 0;
            while (true)
            {
                if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape)
                    break;

                server.NatPunchModule.PollEvents();
                server.PollEvents();

                var now = DateTime.UtcNow;
                foreach (var kv in waitingPeers)
                    if (now - kv.Value.RefreshTime > KickTime)
                        peersToRemove.Add(kv.Key);

                foreach (var key in peersToRemove)
                {
                    Info($"[Server] Stale peer removed: token={key}");
                    Log("Server", $"  age exceeded {KickTime.TotalSeconds}s");
                    waitingPeers.Remove(key);
                }
                peersToRemove.Clear();

                tickCount++;
                if (tickCount % 500 == 0 && waitingPeers.Count > 0)
                {
                    foreach (var kv in waitingPeers)
                        Log("Server", $"Heartbeat: token={kv.Key}  age={(now - kv.Value.RefreshTime).TotalSeconds:F1}s  external={kv.Value.ExternalAddr}");
                }

                Thread.Sleep(10);
            }

            server.Stop();
            Info("[Server] Stopped.");
        }

        // =========================================================================
        // LOCAL TEST
        // =========================================================================
        public static void RunLocalTest()
        {
            Info("=== NAT Punch Local Test (server + 2 clients in one process) ===");
            Log("LocalTest", "Verbose mode on");

            var serverListener = new EventBasedNetListener();
            serverListener.NetworkErrorEvent += (ep, err) => Log("Server", $"NetworkError {ep}: {err}");

            var server = new NetManager(serverListener) { NatPunchEnabled = true, IPv6Enabled = true };
            server.Start(ServerPort);
            Log("Server", $"Started on port {server.LocalPort}");

            var waitingPeers = new Dictionary<string, WaitPeer>();
            var serverNat = new EventBasedNatPunchListener();
            serverNat.NatIntroductionRequest += (localEP, remoteEP, token) =>
            {
                Log("Server", $"NatIntroduceRequest  token={token}  internal={localEP}  external={remoteEP}");
                if (waitingPeers.TryGetValue(token, out var wpeer))
                {
                    Log("Server", $"  Stored: internal={wpeer.InternalAddr}  external={wpeer.ExternalAddr}");
                    if (wpeer.ExternalAddr.Equals(remoteEP))
                    {
                        Log("Server", "  Same peer refresh — skipping");
                        wpeer.Refresh(); return;
                    }
                    Log("Server", "  Second peer arrived — calling NatIntroduce()");
                    server.NatPunchModule.NatIntroduce(wpeer.InternalAddr, wpeer.ExternalAddr, localEP, remoteEP, token);
                    Info($"[Server] Introduced peers for token={token}");
                    waitingPeers.Remove(token);
                }
                else
                {
                    Info($"[Server] First peer waiting  token={token}  external={remoteEP}");
                    waitingPeers[token] = new WaitPeer(localEP, remoteEP);
                }
            };
            server.NatPunchModule.Init(serverNat);

            NetManager MakeClient(string name, out NetPeer[] peerRef)
            {
                var peerHolder = new NetPeer[1];
                peerRef = peerHolder;

                var nl = new EventBasedNetListener();
                nl.ConnectionRequestEvent += req =>
                {
                    Log(name, $"ConnectionRequest from {req.RemoteEndPoint} — accepting");
                    req.AcceptIfKey(ConnectionKey);
                };
                nl.PeerConnectedEvent += p =>
                {
                    peerHolder[0] = p;
                    Info($"[{name}] *** Connected to {p.Address}:{p.Port} ***");
                    Log(name, $"PeerConnected id={p.Id}");
                };
                nl.PeerDisconnectedEvent += (p, info) =>
                {
                    peerHolder[0] = null;
                    Log(name, $"PeerDisconnected: {info.Reason}");
                };
                nl.NetworkReceiveEvent += (p, reader, ch, method) =>
                {
                    HandleReceive(reader, name);
                    reader.Recycle();
                };
                nl.NetworkErrorEvent += (ep, err) => Log(name, $"NetworkError {ep}: {err}");

                var mgr = new NetManager(nl) { NatPunchEnabled = true, IPv6Enabled = true };
                var natL = new EventBasedNatPunchListener();
                natL.NatIntroductionSuccess += (point, addrType, token) =>
                {
                    Info($"[{name}] NatIntroductionSuccess  point={point}  addrType={addrType}");
                    Log(name, $"Calling Connect({point})...");
                    var peer = mgr.Connect(point, ConnectionKey);
                    Log(name, $"Connect() returned: {(peer == null ? "null" : $"peer id={peer.Id}")}");
                };

                mgr.NatPunchModule.Init(natL);
                mgr.Start();
                Log(name, $"Started on local port {mgr.LocalPort}");
                return mgr;
            }

            var c1 = MakeClient("Client1", out var c1Peers);
            var c2 = MakeClient("Client2", out var c2Peers);

            Log("Client1", $"SendNatIntroduceRequest → localhost:{ServerPort} token={Token}");
            c1.NatPunchModule.SendNatIntroduceRequest("localhost", ServerPort, Token);
            Thread.Sleep(200);
            Log("Client2", $"SendNatIntroduceRequest → localhost:{ServerPort} token={Token}");
            c2.NatPunchModule.SendNatIntroduceRequest("localhost", ServerPort, Token);

            Info("Polling for 5 seconds...");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            bool sentMsg = false;
            while (DateTime.UtcNow < deadline)
            {
                server.NatPunchModule.PollEvents(); server.PollEvents();
                c1.NatPunchModule.PollEvents();     c1.PollEvents();
                c2.NatPunchModule.PollEvents();     c2.PollEvents();

                if (!sentMsg && c1Peers[0] != null && c2Peers[0] != null)
                {
                    sentMsg = true;
                    var w = new NetDataWriter();

                    w.Reset(); w.Put(MSG_TEXT); w.Put("Hello from Client1!");
                    c1Peers[0].Send(w, DeliveryMethod.ReliableOrdered);
                    Info("[Client1] Sent text: \"Hello from Client1!\"");

                    w.Reset(); w.Put(MSG_TEXT); w.Put("Hello from Client2!");
                    c2Peers[0].Send(w, DeliveryMethod.ReliableOrdered);
                    Info("[Client2] Sent text: \"Hello from Client2!\"");
                }

                Thread.Sleep(10);
            }

            c1.Stop(); c2.Stop(); server.Stop();
            Info("Done.");
        }

        // =========================================================================
        // CLIENT
        // =========================================================================
        public static void RunClient(string serverHost = null)
        {
            serverHost ??= DefaultServerHost;
            Info($"=== NAT Punch Chat ===  relay={serverHost}:{ServerPort}  token=\"{Token}\"");
            Info("Commands: /file <path>  /quit");
            Log("Client", $"ConnectionKey: \"{ConnectionKey}\"");

            NetManager client = null;
            NetPeer connectedPeer = null;
            var writer = new NetDataWriter();

            var netListener = new EventBasedNetListener();
            netListener.ConnectionRequestEvent += req =>
            {
                Log("Client", $"Incoming connection request from {req.RemoteEndPoint} — accepting if key matches");
                req.AcceptIfKey(ConnectionKey);
            };
            netListener.PeerConnectedEvent += peer =>
            {
                connectedPeer = peer;
                Log("Client", $"PeerConnected: {peer.Address}:{peer.Port}  Id={peer.Id}");
                Info("[Chat] *** Connected! Type a message or /file <path> to send a file ***");
            };
            netListener.PeerDisconnectedEvent += (peer, info) =>
            {
                connectedPeer = null;
                Info($"[Chat] Peer disconnected: {info.Reason}");
                Log("Client", $"PeerDisconnected: {peer.Address}:{peer.Port}  SocketError={info.SocketErrorCode}");
            };
            netListener.NetworkReceiveEvent += (peer, reader, channel, method) =>
            {
                Log("Client", $"NetworkReceive from {peer.Address}:{peer.Port}  channel={channel}  method={method}");
                HandleReceive(reader, "Client");
                reader.Recycle();
            };
            netListener.NetworkErrorEvent += (ep, err) =>
                Log("Client", $"NetworkError from {ep}: {err}");
            netListener.NetworkLatencyUpdateEvent += (peer, latency) =>
                Log("Client", $"Latency update: peer={peer.Address}:{peer.Port}  latency={latency}ms");

            var natListener = new EventBasedNatPunchListener();
            natListener.NatIntroductionSuccess += (point, addrType, token) =>
            {
                Info($"[Chat] NatIntroductionSuccess  point={point}  addrType={addrType}");
                Log("Client", $"Calling Connect({point}, \"{ConnectionKey}\")...");
                var peer = client?.Connect(point, ConnectionKey);
                Log("Client", $"Connect() returned: {(peer == null ? "null (already connected or failed)" : $"peer id={peer.Id}")}");
            };

            client = new NetManager(netListener) { NatPunchEnabled = true, IPv6Enabled = true };
            client.NatPunchModule.Init(natListener);
            client.Start();
            Log("Client", $"UDP socket started. LocalPort={client.LocalPort}");

            Info($"[Chat] Connecting to relay {serverHost}:{ServerPort} ...");
            Log("Client", $"SendNatIntroduceRequest token={Token}");
            client.NatPunchModule.SendNatIntroduceRequest(serverHost, ServerPort, Token);

            var running = true;
            int tickCount = 0;
            var networkThread = new Thread(() =>
            {
                while (running)
                {
                    client.NatPunchModule.PollEvents();
                    client.PollEvents();

                    tickCount++;
                    if (tickCount % 200 == 0 && connectedPeer == null)
                    {
                        Log("Client", $"Re-sending NatIntroduceRequest (tick {tickCount})");
                        client.NatPunchModule.SendNatIntroduceRequest(serverHost, ServerPort, Token);
                    }

                    Thread.Sleep(10);
                }
            }) { IsBackground = true };
            networkThread.Start();

            Log("Client", "Network thread started.");
            Info("Waiting for peer... Type /quit to exit.");
            while (true)
            {
                var line = Console.ReadLine();
                if (line == null || line.Equals("/quit", StringComparison.OrdinalIgnoreCase))
                    break;
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (connectedPeer == null)
                {
                    Info("[Chat] Not connected yet, please wait...");
                    continue;
                }

                if (line.StartsWith("/file ", StringComparison.OrdinalIgnoreCase))
                {
                    var filePath = line.Substring(6).Trim();
                    SendFile(filePath, connectedPeer, "Client");
                }
                else
                {
                    writer.Reset();
                    writer.Put(MSG_TEXT);
                    writer.Put(line);
                    connectedPeer.Send(writer, DeliveryMethod.ReliableOrdered);
                    Log("Client", $"Sent text ({line.Length} chars)");
                }
            }

            running = false;
            client.Stop();
            Log("Client", "Stopped.");
        }
    }
}
