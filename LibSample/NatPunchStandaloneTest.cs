using System;
using System.Collections.Generic;
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

        private static void Log(string tag, string msg) =>
            Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}][{tag}] {msg}");

        public static void RunServer()
        {
            Log("Server", "=== NAT Punch Relay Server ===");
            Log("Server", $"Listening on UDP port {ServerPort}. Token: \"{Token}\"  KickTime: {KickTime.TotalSeconds}s");

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
                    Log("Server", $"  Waiting peer exists for token={token}  stored_internal={wpeer.InternalAddr}  stored_external={wpeer.ExternalAddr}");

                    // same peer refreshing — match on external addr only (internal may differ on retry)
                    if (wpeer.ExternalAddr.Equals(remoteEndPoint))
                    {
                        Log("Server", $"  Same peer refresh (external match) — refreshing timestamp");
                        wpeer.Refresh();
                        return;
                    }

                    Log("Server", $"  Different external addr — treating as second peer. Introducing...");
                    Log("Server", $"  PeerA: internal={wpeer.InternalAddr}  external={wpeer.ExternalAddr}");
                    Log("Server", $"  PeerB: internal={localEndPoint}  external={remoteEndPoint}");
                    server.NatPunchModule.NatIntroduce(
                        wpeer.InternalAddr, wpeer.ExternalAddr,
                        localEndPoint,      remoteEndPoint,
                        token);
                    Log("Server", $"  NatIntroduce sent. Removing token={token} from waiting list.");
                    waitingPeers.Remove(token);
                }
                else
                {
                    Log("Server", $"  No waiting peer for token={token} — registering as first peer.");
                    waitingPeers[token] = new WaitPeer(localEndPoint, remoteEndPoint);
                    Log("Server", $"  WaitingPeers count: {waitingPeers.Count}");
                }
            };

            server.NatPunchModule.Init(natListener);

            Log("Server", "Running. Press ESC to stop.");
            int tickCount = 0;
            while (true)
            {
                if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape)
                    break;

                server.NatPunchModule.PollEvents();
                server.PollEvents();

                var now = DateTime.UtcNow;
                foreach (var kv in waitingPeers)
                {
                    var age = now - kv.Value.RefreshTime;
                    if (age > KickTime)
                        peersToRemove.Add(kv.Key);
                }

                foreach (var key in peersToRemove)
                {
                    Log("Server", $"Stale peer removed: token={key} (age exceeded {KickTime.TotalSeconds}s)");
                    waitingPeers.Remove(key);
                }
                peersToRemove.Clear();

                // log waiting peer ages every ~5 seconds
                tickCount++;
                if (tickCount % 500 == 0 && waitingPeers.Count > 0)
                {
                    foreach (var kv in waitingPeers)
                        Log("Server", $"  Heartbeat: token={kv.Key}  age={(now - kv.Value.RefreshTime).TotalSeconds:F1}s  external={kv.Value.ExternalAddr}");
                }

                Thread.Sleep(10);
            }

            server.Stop();
            Log("Server", "Stopped.");
        }

        public static void RunLocalTest()
        {
            Log("LocalTest", "=== NAT Punch Local Test (server + 2 clients in one process) ===");

            // ---- Server ----
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
                    Log("Server", "  NatIntroduce sent — removing token from waiting list");
                    waitingPeers.Remove(token);
                }
                else
                {
                    Log("Server", "  First peer — registering in waiting list");
                    waitingPeers[token] = new WaitPeer(localEP, remoteEP);
                }
            };
            server.NatPunchModule.Init(serverNat);

            // ---- Client factory ----
            NetManager MakeClient(string name, out NetPeer[] peerRef)
            {
                var peerHolder = new NetPeer[1];
                peerRef = peerHolder;

                var nl = new EventBasedNetListener();
                nl.ConnectionRequestEvent += req => { Log(name, $"ConnectionRequest from {req.RemoteEndPoint} — accepting"); req.AcceptIfKey(ConnectionKey); };
                nl.PeerConnectedEvent     += p  => { peerHolder[0] = p; Log(name, $"*** PeerConnected: {p.Address}:{p.Port} ***"); };
                nl.PeerDisconnectedEvent  += (p, info) => { peerHolder[0] = null; Log(name, $"PeerDisconnected: {info.Reason}"); };
                nl.NetworkReceiveEvent    += (p, reader, ch, method) => { Log(name, $"Recv: \"{reader.GetString()}\""); reader.Recycle(); };
                nl.NetworkErrorEvent      += (ep, err) => Log(name, $"NetworkError {ep}: {err}");

                var mgr = new NetManager(nl) { NatPunchEnabled = true, IPv6Enabled = true };

                var natL = new EventBasedNatPunchListener();
                natL.NatIntroductionSuccess += (point, addrType, token) =>
                {
                    Log(name, $"NatIntroductionSuccess  point={point}  addrType={addrType}  token={token}");
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

            // Poll loop for 5 seconds, show connected state
            Log("LocalTest", "Polling for 5 seconds...");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            bool sentMsg = false;
            while (DateTime.UtcNow < deadline)
            {
                server.NatPunchModule.PollEvents(); server.PollEvents();
                c1.NatPunchModule.PollEvents();     c1.PollEvents();
                c2.NatPunchModule.PollEvents();     c2.PollEvents();

                // Once both are connected, send one test message each direction
                if (!sentMsg && c1Peers[0] != null && c2Peers[0] != null)
                {
                    sentMsg = true;
                    var w = new NetDataWriter();
                    w.Put("Hello from Client1!");
                    c1Peers[0].Send(w, DeliveryMethod.ReliableOrdered);
                    Log("Client1", "Sent: \"Hello from Client1!\"");

                    w.Reset(); w.Put("Hello from Client2!");
                    c2Peers[0].Send(w, DeliveryMethod.ReliableOrdered);
                    Log("Client2", "Sent: \"Hello from Client2!\"");
                }

                Thread.Sleep(10);
            }

            c1.Stop(); c2.Stop(); server.Stop();
            Log("LocalTest", "Done.");
        }

        public static void RunClient(string serverHost = null)
        {
            serverHost ??= DefaultServerHost;
            Log("Client", "=== NAT Punch Chat ===");
            Log("Client", $"Relay: {serverHost}:{ServerPort}  Token: \"{Token}\"  ConnectionKey: \"{ConnectionKey}\"");

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
                Log("Client", $"PeerConnected: {peer.Address}:{peer.Port}  Id={peer.Id} — start typing!");
                Console.WriteLine("[Chat] *** Connected! Start typing your message and press Enter ***");
            };
            netListener.PeerDisconnectedEvent += (peer, info) =>
            {
                connectedPeer = null;
                Log("Client", $"PeerDisconnected: {peer.Address}:{peer.Port}  Reason={info.Reason}  SocketError={info.SocketErrorCode}");
            };
            netListener.NetworkReceiveEvent += (peer, reader, channel, method) =>
            {
                var msg = reader.GetString();
                Log("Client", $"NetworkReceive from {peer.Address}:{peer.Port}  channel={channel}  method={method}  len={msg.Length}");
                Console.WriteLine($"[Peer] {msg}");
                reader.Recycle();
            };
            netListener.NetworkErrorEvent += (ep, err) =>
                Log("Client", $"NetworkError from {ep}: {err}");
            netListener.NetworkLatencyUpdateEvent += (peer, latency) =>
                Log("Client", $"Latency update: peer={peer.Address}:{peer.Port}  latency={latency}ms");

            var natListener = new EventBasedNatPunchListener();
            natListener.NatIntroductionSuccess += (point, addrType, token) =>
            {
                Log("Client", $"NatIntroductionSuccess  point={point}  addrType={addrType}  token={token}");
                Log("Client", $"Calling client.Connect({point}, \"{ConnectionKey}\")...");
                var peer = client?.Connect(point, ConnectionKey);
                Log("Client", $"Connect() returned: {(peer == null ? "null (already connected or failed)" : $"peer id={peer.Id}")}");
            };

            client = new NetManager(netListener) { NatPunchEnabled = true, IPv6Enabled = true };
            client.NatPunchModule.Init(natListener);
            client.Start();
            Log("Client", $"UDP socket started. LocalPort={client.LocalPort}");

            Log("Client", $"Sending NatIntroduceRequest to {serverHost}:{ServerPort} token={Token}");
            client.NatPunchModule.SendNatIntroduceRequest(serverHost, ServerPort, Token);

            var running = true;
            int tickCount = 0;
            var networkThread = new Thread(() =>
            {
                while (running)
                {
                    client.NatPunchModule.PollEvents();
                    client.PollEvents();

                    // re-send NAT introduce request every 2s while not connected (keeps hole open)
                    tickCount++;
                    if (tickCount % 200 == 0 && connectedPeer == null)
                    {
                        Log("Client", $"Re-sending NatIntroduceRequest (tick {tickCount}) ...");
                        client.NatPunchModule.SendNatIntroduceRequest(serverHost, ServerPort, Token);
                    }

                    Thread.Sleep(10);
                }
            }) { IsBackground = true };
            networkThread.Start();

            Log("Client", "Network thread started. Waiting for peer...");
            Console.WriteLine("Waiting for peer... Type /quit to exit.");
            while (true)
            {
                var line = Console.ReadLine();
                if (line == null || line.Equals("/quit", StringComparison.OrdinalIgnoreCase))
                    break;
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (connectedPeer != null)
                {
                    writer.Reset();
                    writer.Put(line);
                    connectedPeer.Send(writer, DeliveryMethod.ReliableOrdered);
                    Log("Client", $"Sent message ({line.Length} chars) to {connectedPeer.Address}:{connectedPeer.Port}");
                }
                else
                {
                    Console.WriteLine("[Chat] Not connected yet, please wait...");
                }
            }

            running = false;
            client.Stop();
            Log("Client", "Stopped.");
        }
    }
}
