using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace HyperTizen.Bulbs.Matter
{
    /// <summary>Minimal Matter controller: one CASE session per bulb, MRP, and IM Invoke. One UDP socket; responses are
    /// routed by local session id and exchange id. Calls to one node are serialized, different nodes run in parallel.</summary>
    public sealed class MatterController : IBulbTransport, IDisposable
    {
        private const int Port = 5540;
        private const ushort ProtoSecureChannel = 0x0000, ProtoIm = 0x0001;
        private const byte OpStandaloneAck = 0x10, OpStatusReport = 0x40;
        private const byte OpStatusResponse = 0x01, OpInvokeRequest = 0x08, OpInvokeResponse = 0x09;

        private sealed class Node
        {
            public BulbEntry Bulb;
            public readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
            public SecureSession Session;
        }

        private sealed class Msg
        {
            public ProtocolHeader Proto;
            public byte[] Body;
            public uint Counter; // the peer's message counter
        }

        private sealed class Pending
        {
            public uint Counter;
            public uint? Routed; // peer counter already handled on this exchange; a copy of it is a duplicate
            public readonly TaskCompletionSource<bool> Acked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<Msg> Response = new TaskCompletionSource<Msg>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private readonly MatterCredentials _creds;
        private readonly string _interface;
        private readonly Action<string> _log;
        private readonly Dictionary<ulong, Node> _nodes;
        private readonly ConcurrentDictionary<ushort, SecureSession> _sessions = new ConcurrentDictionary<ushort, SecureSession>();
        private readonly ConcurrentDictionary<(ushort, ushort), Pending> _pending = new ConcurrentDictionary<(ushort, ushort), Pending>();
        private readonly object _socketLock = new object();
        private Func<byte[], IPEndPoint, Task> _send;
        private UdpClient _udp;
        private int _exchangeId, _sessionId, _unsecuredCounter;
        private volatile bool _disposed;

        /// <summary>MRP base retransmission interval; a knob for tests and lossy networks.</summary>
        internal TimeSpan BaseInterval = Mrp.ActiveInterval;

        /// <summary>How long to wait for the response once the request is acked (matter.js: 2 s expected processing + margin).</summary>
        internal TimeSpan ResponseTimeout = TimeSpan.FromSeconds(3);

        /// <param name="interfaceScope">network interface name ("wlan0", "en0"), resolved to the IPv6 scope index when connecting</param>
        public MatterController(MatterCredentials creds, string interfaceScope, Action<string> log)
            : this(creds, (Func<byte[], IPEndPoint, Task>)null, log)
        {
            _interface = interfaceScope;
        }

        /// <summary>For tests: datagrams go to <paramref name="send"/>; feed replies to <see cref="OnDatagram"/>.</summary>
        internal MatterController(MatterCredentials creds, Func<byte[], IPEndPoint, Task> send, Action<string> log)
        {
            _creds = creds;
            _send = send;
            _log = log ?? (_ => { });
            _nodes = creds.Bulbs.ToDictionary(b => b.NodeId, b => new Node { Bulb = b });
            var r = SessionCrypto.RandomBytes(4);
            _exchangeId = Bin.Get16(r, 0);
            _sessionId = Bin.Get16(r, 2);
            _unsecuredCounter = (int)(BitConverter.ToUInt32(SessionCrypto.RandomBytes(4), 0) & 0x0fffffff);
        }

        internal void InstallSession(ulong node, SecureSession s)
        {
            _nodes[node].Session = s;
            _sessions[s.LocalId] = s;
        }

        public Task<bool> MoveToHueAndSaturation(ulong node, int hue, int sat, int tenths, CancellationToken ct) =>
            Invoke(node, 0x0300, 0x06, ct, Mrp.MaxTransmissions, (ulong)hue, (ulong)sat, (ulong)tenths, 0UL, 0UL);

        public Task<bool> MoveToLevelWithOnOff(ulong node, int level, int tenths, CancellationToken ct, bool failFast = false) =>
            Invoke(node, 0x0008, 0x04, ct, Budget(failFast), (ulong)level, (ulong)tenths, 0UL, 0UL);

        public Task<bool> MoveToColorTemperature(ulong node, int mireds, int tenths, CancellationToken ct, bool failFast = false) =>
            Invoke(node, 0x0300, 0x0a, ct, Budget(failFast), (ulong)mireds, (ulong)tenths, 0UL, 0UL);

        private static int Budget(bool failFast) => failFast ? Mrp.FailFastTransmissions : Mrp.MaxTransmissions;

        /// <param name="transmissions">MRP budget for the invoke itself; CASE always gets the full one</param>
        private async Task<bool> Invoke(ulong nodeId, ulong cluster, ulong command, CancellationToken ct, int transmissions, params ulong[] fields)
        {
            if (!_nodes.TryGetValue(nodeId, out var node)) throw new ArgumentException("unknown node " + nodeId, nameof(nodeId));
            await node.Gate.WaitAsync(ct).ConfigureAwait(false);
            bool drop = false;
            try
            {
                if (node.Session == null) node.Session = await Connect(node, ct).ConfigureAwait(false);
                if (node.Session == null) return false;

                var resp = await Request(node.Session, NextExchange(), ProtoIm, OpInvokeRequest, InvokeRequest(cluster, command, fields), 0, ct,
                    transmissions: transmissions).ConfigureAwait(false);
                int status = resp == null ? -1 : InvokeStatus(resp);
                if (status == 0) return true;
                _log("matter: node " + nodeId + " invoke " + (resp == null ? "unconfirmed" : Describe(resp) + ", IM status " + status) + ", dropping session " + node.Session.LocalId);
                drop = true;
                return false;
            }
            catch (Exception e) when (!(e is OperationCanceledException) && !_disposed)
            {
                _log("matter: node " + nodeId + " invoke failed: " + e.GetType().Name);
                drop = true;
                return false;
            }
            finally
            {
                if (drop) Drop(node); // a cancelled call keeps its session: nothing says the session is broken
                node.Gate.Release();
            }
        }

        private void Drop(Node node)
        {
            var s = node.Session;
            node.Session = null;
            if (s != null) _sessions.TryRemove(s.LocalId, out _);
        }

        private async Task<SecureSession> Connect(Node node, CancellationToken ct)
        {
            uint scope = _interface == null ? 0 : ScopeIndex(_interface);
            foreach (var a in node.Bulb.Addresses)
            {
                var ip = IPAddress.Parse(a);
                if (ip.IsIPv6LinkLocal) ip.ScopeId = scope;
                var ep = new IPEndPoint(ip, Port);
                ushort localId;
                do localId = (ushort)Interlocked.Increment(ref _sessionId); while (localId == 0 || _sessions.ContainsKey(localId));

                var cs = new CaseInitiator(_creds, node.Bulb.NodeId, localId);
                ulong eph = BitConverter.ToUInt64(SessionCrypto.RandomBytes(8), 0) & 0xFFFFFFEFFFFFFFFF; // operational node id range
                ushort exch = NextExchange();
                _log("matter: node " + node.Bulb.NodeId + " CASE Sigma1 to " + ep + ", session " + localId);
                var s2 = await Request(null, exch, ProtoSecureChannel, CaseInitiator.OpSigma1, cs.Sigma1(), eph, ct, ep).ConfigureAwait(false);
                if (s2 == null) { _log("matter: node " + node.Bulb.NodeId + " no answer to Sigma1 at " + ep); continue; }
                if (s2.Proto.Opcode != CaseInitiator.OpSigma2)
                {
                    _log("matter: node " + node.Bulb.NodeId + " Sigma1 rejected: " + Describe(s2));
                    return null;
                }
                _log("matter: node " + node.Bulb.NodeId + " Sigma2 " + s2.Body.Length + " bytes");
                byte[] s3;
                try { s3 = cs.Sigma3(s2.Body); }
                catch (CaseException e) { _log("matter: node " + node.Bulb.NodeId + " bad Sigma2: " + e.Message); return null; }

                // Sigma3 acks Sigma2 (as matter.js does), and that counter marks Sigma2 retransmits as duplicates
                var st = await Request(null, exch, ProtoSecureChannel, CaseInitiator.OpSigma3, s3, eph, ct, ep, s2.Counter).ConfigureAwait(false);
                if (st == null || st.Proto.Opcode != OpStatusReport || st.Body.Length < 8 || Bin.Get16(st.Body, 0) != 0 || Bin.Get16(st.Body, 6) != 0)
                {
                    _log("matter: node " + node.Bulb.NodeId + " Sigma3 not accepted: " + (st == null ? "no answer" : Describe(st)));
                    return null;
                }
                var s = cs.Finish(ep);
                _sessions[s.LocalId] = s;
                _log("matter: node " + node.Bulb.NodeId + " CASE up, session " + s.LocalId + " / peer " + s.PeerId);
                return s;
            }
            return null;
        }

        private static string Describe(Msg m) =>
            m.Proto.Opcode == OpStatusReport && m.Body.Length >= 8
                ? "status report general " + Bin.Get16(m.Body, 0) + " protocol 0x" + Bin.Get32(m.Body, 2).ToString("x") + " code " + Bin.Get16(m.Body, 6)
                : "opcode 0x" + m.Proto.Opcode.ToString("x2") + " protocol " + m.Proto.ProtocolId + ", " + m.Body.Length + " bytes";

        /// <summary>Sends a reliable message on a new exchange and waits for the response. Returns null when MRP gives up
        /// or no response follows the ack in time.</summary>
        /// <param name="s">null for the unsecured session (CASE), with <paramref name="eph"/> our ephemeral node id</param>
        /// <param name="ackCounter">the peer message on this exchange this one acknowledges; copies of it are then ignored</param>
        private async Task<Msg> Request(SecureSession s, ushort exch, ushort proto, byte opcode, byte[] body, ulong eph, CancellationToken ct,
            IPEndPoint ep = null, uint? ackCounter = null, int transmissions = Mrp.MaxTransmissions)
        {
            EnsureSocket();
            var p = new Pending { Routed = ackCounter };
            var key = (s?.LocalId ?? 0, exch);
            _pending[key] = p;
            try
            {
                p.Counter = s?.NextCounter() ?? (uint)Interlocked.Increment(ref _unsecuredCounter);
                var ph = new ProtocolHeader { Initiator = true, NeedsAck = true, Opcode = opcode, ExchangeId = exch, ProtocolId = proto, AckCounter = ackCounter }.Encode();
                var payload = new byte[ph.Length + body.Length];
                Buffer.BlockCopy(ph, 0, payload, 0, ph.Length);
                Buffer.BlockCopy(body, 0, payload, ph.Length, body.Length);
                var d = s == null
                    ? MessageCodec.EncodeUnsecured(p.Counter, payload, eph, null)
                    : MessageCodec.EncodeSecured(s.PeerId, p.Counter, payload, s.EncryptKey, _creds.NodeId);
                ep = ep ?? s.Endpoint;

                // retransmissions resend the identical datagram: same counter, so the peer sees a duplicate, not a new message
                for (int n = 0; n < transmissions && !p.Acked.Task.IsCompleted; n++)
                {
                    await _send(d, ep).ConfigureAwait(false);
                    await Task.WhenAny(p.Acked.Task, Task.Delay(Mrp.Backoff(n, BaseInterval), ct)).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                }
                if (!p.Acked.Task.IsCompleted) return null;
                await Task.WhenAny(p.Response.Task, Task.Delay(ResponseTimeout, ct)).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                return p.Response.Task.IsCompleted ? p.Response.Task.Result : null;
            }
            finally
            {
                _pending.TryRemove(key, out _);
            }
        }

        /// <summary>Handles one inbound datagram: decrypts, acks it if it asks for one, and routes it to its pending exchange.</summary>
        /// <param name="from">the sender; needed only to ack unsecured (CASE) messages</param>
        internal void OnDatagram(byte[] d, IPEndPoint from = null)
        {
            try
            {
                if (d.Length < 8) return;
                ushort sid = Bin.Get16(d, 1);
                SecureSession s = null;
                MessageHeader h;
                byte[] plain;
                if (sid == 0) plain = MessageCodec.DecodeUnsecured(d, out h);
                else if (_sessions.TryGetValue(sid, out s)) plain = MessageCodec.DecodeSecured(d, s.DecryptKey, s.PeerNodeId, out h);
                else return; // a session we dropped
                // ponytail: no per-session duplicate window (Core spec §4.6.5). A duplicate is re-acked; it is dropped if its
                // exchange is finished, or if its counter is the one already routed on the pending exchange (Pending.Routed:
                // e.g. a Sigma2 retransmit while Sigma3 waits on the same exchange). Upgrade: a counter window per session.

                var proto = ProtocolHeader.Decode(plain, out int off);
                if (proto.NeedsAck) SendAck(s, h, proto, from);
                if (!_pending.TryGetValue((sid, proto.ExchangeId), out var p) || p.Routed == h.Counter) return;
                bool standaloneAck = proto.ProtocolId == ProtoSecureChannel && proto.Opcode == OpStandaloneAck;
                if (proto.AckCounter == p.Counter || !standaloneAck) p.Acked.TrySetResult(true);
                if (!standaloneAck)
                {
                    var body = new byte[plain.Length - off];
                    Buffer.BlockCopy(plain, off, body, 0, body.Length);
                    p.Routed = h.Counter;
                    p.Response.TrySetResult(new Msg { Proto = proto, Body = body, Counter = h.Counter });
                }
            }
            catch (Exception e)
            {
                _log("matter: dropped inbound " + d.Length + "-byte datagram: " + e.GetType().Name);
            }
        }

        private void SendAck(SecureSession s, MessageHeader h, ProtocolHeader proto, IPEndPoint from)
        {
            var ph = new ProtocolHeader
            {
                Initiator = !proto.Initiator, Opcode = OpStandaloneAck, ExchangeId = proto.ExchangeId,
                ProtocolId = ProtoSecureChannel, AckCounter = h.Counter,
            }.Encode();
            byte[] d;
            IPEndPoint ep;
            if (s == null)
            {
                // the reply to our unsecured messages carries our ephemeral node id as its destination
                d = MessageCodec.EncodeUnsecured((uint)Interlocked.Increment(ref _unsecuredCounter), ph, h.DestNodeId, h.SourceNodeId);
                ep = from;
                if (ep == null) return;
            }
            else
            {
                d = MessageCodec.EncodeSecured(s.PeerId, s.NextCounter(), ph, s.EncryptKey, _creds.NodeId);
                ep = s.Endpoint;
            }
            _ = _send(d, ep);
        }

        private void EnsureSocket()
        {
            if (_send != null) return;
            lock (_socketLock)
            {
                if (_send != null) return;
                if (_disposed) throw new ObjectDisposedException(nameof(MatterController));
                _udp = new UdpClient(AddressFamily.InterNetworkV6);
                _udp.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
                var udp = _udp;
                _ = Task.Run(() => ReceiveLoop(udp));
                _send = (d, ep) => udp.SendAsync(d, d.Length, ep);
            }
        }

        private async Task ReceiveLoop(UdpClient udp)
        {
            while (!_disposed)
            {
                UdpReceiveResult r;
                try { r = await udp.ReceiveAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { return; }
                catch (SocketException e)
                {
                    if (_disposed) return;
                    _log("matter: receive error " + e.SocketErrorCode);
                    continue;
                }
                OnDatagram(r.Buffer, r.RemoteEndPoint);
            }
        }

        private static uint ScopeIndex(string name)
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(i => i.Name == name)
                ?? throw new InvalidOperationException("no network interface " + name);
            return (uint)nic.GetIPProperties().GetIPv6Properties().Index;
        }

        private ushort NextExchange() => (ushort)Interlocked.Increment(ref _exchangeId);

        /// <summary>InvokeRequestMessage (Interaction Model §8.8.2), laid out as matter.js writes it.</summary>
        private static byte[] InvokeRequest(ulong cluster, ulong command, ulong[] fields)
        {
            var w = new TlvWriter();
            w.StartStructure(TlvWriter.Anonymous);
            w.Put(0, false);                 // suppressResponse
            w.Put(1, false);                 // timedRequest
            w.StartArray(2);                 // invokeRequests
            w.StartStructure(TlvWriter.Anonymous);
            w.StartList(0);                  // CommandPathIB
            w.Put(0, 1UL);                   // endpoint
            w.Put(1, cluster);
            w.Put(2, command);
            w.EndContainer();
            w.StartStructure(1);             // commandFields
            for (int i = 0; i < fields.Length; i++) w.Put(i, fields[i]);
            w.EndContainer();
            w.EndContainer();
            w.EndContainer();
            w.Put(255, 11UL);                // interactionModelRevision
            w.EndContainer();
            return w.ToArray();
        }

        /// <returns>the IM status of the first InvokeResponseIB (0 = SUCCESS), or -1 when there is none</returns>
        private static int InvokeStatus(Msg m)
        {
            if (m.Proto.ProtocolId != ProtoIm) return -1;
            var r = new TlvReader(m.Body);
            if (!r.Next() || r.Type != TlvType.Structure) return -1;
            r.EnterContainer();
            while (r.Next())
            {
                if (m.Proto.Opcode == OpStatusResponse && r.Tag == 0) return (int)r.GetUInt(); // StatusResponseMessage
                if (m.Proto.Opcode != OpInvokeResponse || r.Tag != 1) continue;
                r.EnterContainer();                                  // invokeResponses
                if (!r.Next()) return -1;
                r.EnterContainer();                                  // InvokeResponseIB
                while (r.Next())
                {
                    if (r.Tag != 1) continue;                        // 0 would be a command response with data
                    r.EnterContainer();                              // CommandStatusIB
                    while (r.Next())
                    {
                        if (r.Tag != 1) continue;
                        r.EnterContainer();                          // StatusIB
                        while (r.Next()) if (r.Tag == 0) return (int)r.GetUInt();
                        return -1;
                    }
                    return -1;
                }
                return -1;
            }
            return -1;
        }

        public void Dispose()
        {
            _disposed = true;
            lock (_socketLock) _udp?.Dispose();
        }
    }
}
