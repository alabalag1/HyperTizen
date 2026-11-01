using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using HyperTizen.Bulbs.Matter;
using Xunit;

namespace HyperTizen.Bulbs.Tests
{
    /// <summary>MatterController over an in-memory UDP loop: a fake bulb that already shares a CASE session with it.</summary>
    public class MatterControllerTests
    {
        private static readonly MatterCredentials Creds =
            MatterCredentials.Load(Path.Combine(AppContext.BaseDirectory, "fixtures", "credentials-fake.json"));

        private sealed class Sent
        {
            public MessageHeader Header;
            public ProtocolHeader Proto;
            public byte[] Body;
        }

        /// <summary>CASE responder side (Core spec §4.14.2), written from the spec independently of CaseInitiator. The NOC
        /// is a minimal Matter TLV certificate: subject (tag 6: 17 node id, 21 fabric id) and public key (tag 9).</summary>
        private sealed class CaseResponder
        {
            public ulong NocNode = 1, NocFabric = Creds.FabricId;
            public bool FlipSignature;
            public const ushort SessionId = 0x2222;
            private byte[] _s1, _s2, _shared;
            public ushort InitiatorSession;

            public byte[] Sigma2(byte[] sigma1)
            {
                _s1 = sigma1;
                byte[] initEph = null;
                var r = new TlvReader(sigma1);
                r.Next(); r.EnterContainer();
                while (r.Next())
                {
                    if (r.Tag == 2) InitiatorSession = (ushort)r.GetUInt();
                    if (r.Tag == 4) initEph = r.GetBytes();
                }
                var (ephPriv, ephPub) = SessionCrypto.GenerateP256();
                var (nocPriv, nocPub) = SessionCrypto.GenerateP256();
                _shared = SessionCrypto.EcdhP256(ephPriv, initEph);
                var random = SessionCrypto.RandomBytes(32);

                var w = new TlvWriter();
                w.StartStructure(Anon);
                w.StartList(6); w.Put(17, NocNode); w.Put(21, NocFabric); w.EndContainer();
                w.Put(9, nocPub);
                w.EndContainer();
                var noc = w.ToArray();

                w = new TlvWriter();
                w.StartStructure(Anon); w.Put(1, noc); w.Put(3, ephPub); w.Put(4, initEph); w.EndContainer();
                var sig = SessionCrypto.SignP256(nocPriv, w.ToArray());
                if (FlipSignature) sig[10] ^= 0x01;

                w = new TlvWriter();
                w.StartStructure(Anon); w.Put(1, noc); w.Put(3, sig); w.Put(4, new byte[16]); w.EndContainer();
                var key = SessionCrypto.Hkdf(_shared, Cat(Creds.Ipk, random, ephPub, SessionCrypto.Sha256(sigma1)), Ascii("Sigma2"), 16);
                var enc = MessageCodec.Ccm(true, key, Ascii("NCASE_Sigma2N"), null, w.ToArray());

                w = new TlvWriter();
                w.StartStructure(Anon); w.Put(1, random); w.Put(2, (ulong)SessionId); w.Put(3, ephPub); w.Put(4, enc); w.EndContainer();
                return _s2 = w.ToArray();
            }

            /// <summary>Decrypts Sigma3 (throws on a MIC failure) and returns the session keys (I2R, R2I).</summary>
            public (byte[], byte[]) Finish(byte[] sigma3)
            {
                var r = new TlvReader(sigma3);
                r.Next(); r.EnterContainer(); r.Next();
                var key = SessionCrypto.Hkdf(_shared, Cat(Creds.Ipk, SessionCrypto.Sha256(Cat(_s1, _s2))), Ascii("Sigma3"), 16);
                MessageCodec.Ccm(false, key, Ascii("NCASE_Sigma3N"), null, r.GetBytes());
                var keys = SessionCrypto.Hkdf(_shared, Cat(Creds.Ipk, SessionCrypto.Sha256(Cat(_s1, _s2, sigma3))), Ascii("SessionKeys"), 48);
                return (keys.Take(16).ToArray(), keys.Skip(16).Take(16).ToArray());
            }

            private static byte[] Ascii(string s) => System.Text.Encoding.ASCII.GetBytes(s);
            private static byte[] Cat(params byte[][] p) => p.SelectMany(x => x).ToArray();
        }

        private const int Anon = TlvWriter.Anonymous;

        /// <summary>Answers InvokeRequests with SUCCESS on its session; <see cref="Drop"/> loses datagrams. With
        /// <see cref="Case"/> set it also answers CASE; otherwise a session is pre-installed.</summary>
        private sealed class FakeBulb
        {
            public byte[] I2R = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
            public byte[] R2I = Enumerable.Range(16, 16).Select(i => (byte)i).ToArray();
            public ushort CtrlSession = 0x1111;
            public const ushort BulbSession = CaseResponder.SessionId;
            public const ulong Node = 1;
            public readonly List<Sent> Received = new List<Sent>();
            public Func<Sent, bool> Drop = _ => false;
            public bool Down; // the network is down: every send throws at once
            public readonly List<string> Log = new List<string>();
            public MatterController C;
            public CaseResponder Case;
            public bool ResendSigma2BeforeStatus;
            public int Sigma2DelayMs; // > 0: ack Sigma1 at once, answer it this much later (slow bulb crypto)
            private uint _counter = 500;
            private byte[] _sigma2Datagram;

            public FakeBulb(CaseResponder caseResponder = null)
            {
                Case = caseResponder;
                C = new MatterController(Creds, Send, Log.Add) { BaseInterval = TimeSpan.FromMilliseconds(10), ResponseTimeout = TimeSpan.FromMilliseconds(200) };
                if (Case == null)
                    C.InstallSession(Node, new SecureSession(CtrlSession, BulbSession, Node, I2R, R2I, new IPEndPoint(IPAddress.IPv6Loopback, 5540)));
            }

            private void AnswerCase(Sent s, byte[] body)
            {
                byte[] Reply(byte opcode, byte[] payload) => MessageCodec.EncodeUnsecured(_counter++,
                    new ProtocolHeader { Opcode = opcode, ExchangeId = s.Proto.ExchangeId, ProtocolId = 0, NeedsAck = true, AckCounter = s.Header.Counter }
                        .Encode().Concat(payload).ToArray(), null, s.Header.SourceNodeId);
                if (s.Proto.Opcode == 0x30 && Sigma2DelayMs > 0)
                {
                    C.OnDatagram(MessageCodec.EncodeUnsecured(_counter++, new ProtocolHeader
                        { Opcode = 0x10, ExchangeId = s.Proto.ExchangeId, ProtocolId = 0, AckCounter = s.Header.Counter }.Encode(), null, s.Header.SourceNodeId));
                    _ = Task.Delay(Sigma2DelayMs).ContinueWith(_ => C.OnDatagram(_sigma2Datagram = Reply(0x31, Case.Sigma2(body))));
                }
                else if (s.Proto.Opcode == 0x30)
                {
                    C.OnDatagram(_sigma2Datagram = Reply(0x31, Case.Sigma2(body)));
                }
                else if (s.Proto.Opcode == 0x32)
                {
                    (I2R, R2I) = Case.Finish(body);
                    CtrlSession = Case.InitiatorSession;
                    if (ResendSigma2BeforeStatus) C.OnDatagram(_sigma2Datagram); // MRP retransmit: our ack was lost
                    C.OnDatagram(Reply(0x40, new byte[8])); // StatusReport SUCCESS: general 0, protocol 0, code 0
                }
            }

            public IEnumerable<Sent> Invokes => Received.Where(s => s.Proto.ProtocolId == 1 && s.Proto.Opcode == 0x08);

            private Task Send(byte[] d, IPEndPoint ep)
            {
                if (Down) throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.NetworkUnreachable);
                var s = new Sent();
                byte[] plain = Bin.Get16(d, 1) == 0
                    ? MessageCodec.DecodeUnsecured(d, out s.Header)
                    : MessageCodec.DecodeSecured(d, I2R, Creds.NodeId, out s.Header);
                s.Proto = ProtocolHeader.Decode(plain, out int off);
                s.Body = plain.Skip(off).ToArray();
                lock (Received) Received.Add(s);
                if (s.Header.SessionId == 0 && Case != null && !Drop(s)) { AnswerCase(s, s.Body); return Task.CompletedTask; }
                if (s.Header.SessionId != BulbSession || Drop(s) || s.Proto.Opcode != 0x08) return Task.CompletedTask;

                // InvokeResponse { 0: false, 1: [ { 1: CommandStatusIB { 0: path, 1: { 0: SUCCESS } } } ], 255: 11 }
                var w = new TlvWriter();
                w.StartStructure(Anon);
                w.Put(0, false);
                w.StartArray(1);
                w.StartStructure(TlvWriter.Anonymous);
                w.StartStructure(1);
                w.StartList(0); w.Put(0, 1UL); w.Put(1, 0x0300UL); w.Put(2, 6UL); w.EndContainer();
                w.StartStructure(1); w.Put(0, 0UL); w.EndContainer();
                w.EndContainer();
                w.EndContainer();
                w.EndContainer();
                w.Put(255, 11UL);
                w.EndContainer();
                var ph = new ProtocolHeader { Opcode = 0x09, ExchangeId = s.Proto.ExchangeId, ProtocolId = 1, NeedsAck = true, AckCounter = s.Header.Counter }.Encode();
                var reply = MessageCodec.EncodeSecured(CtrlSession, _counter++, ph.Concat(w.ToArray()).ToArray(), R2I, Node);
                C.OnDatagram(reply);
                return Task.CompletedTask;
            }
        }

        [Fact]
        public async Task Mrp_RetransmitsUntilAck()
        {
            var b = new FakeBulb();
            int seen = 0;
            b.Drop = s => s.Proto.Opcode == 0x08 && seen++ == 0;
            Assert.True(await b.C.MoveToHueAndSaturation(1, 200, 254, 20, CancellationToken.None));
            var inv = b.Invokes.ToList();
            Assert.Equal(2, inv.Count);
            Assert.Equal(inv[0].Header.Counter, inv[1].Header.Counter); // a retransmit, not a new message
            Assert.Contains(b.Received, s => s.Proto.Opcode == 0x10 && s.Proto.ProtocolId == 0); // standalone ack for the response
        }

        [Fact]
        public async Task Mrp_GivesUpAfterMaxRetries_ReturnsFalse()
        {
            var b = new FakeBulb();
            b.Drop = _ => true;
            Assert.False(await b.C.MoveToHueAndSaturation(1, 200, 254, 20, CancellationToken.None));
            Assert.Equal(Mrp.MaxTransmissions, b.Invokes.Count());
        }

        [Fact]
        public async Task Mrp_FailFast_GivesUpAfterTwo()
        {
            var b = new FakeBulb();
            b.Drop = _ => true;
            Assert.False(await b.C.MoveToLevelWithOnOff(1, 127, 10, CancellationToken.None, failFast: true));
            Assert.Equal(2, b.Invokes.Count());
        }

        [Fact]
        public async Task Outage_LogsSampled_RecoveryLogged()
        {
            var b = new FakeBulb(new CaseResponder()) { Down = true };
            for (int i = 0; i < 40; i++) Assert.False(await b.C.MoveToLevelWithOnOff(1, 128, 0, CancellationToken.None));
            Assert.InRange(b.Log.Count, 1, 6);
            b.Down = false;
            Assert.True(await b.C.MoveToLevelWithOnOff(1, 128, 0, CancellationToken.None));
            Assert.Contains(b.Log, l => l.Contains("recovered"));
        }

        [Fact]
        public async Task NoAnswerToSigma1_LogsSampled()
        {
            var b = new FakeBulb(new CaseResponder());
            b.Drop = _ => true;                     // bulb unplugged, network up
            b.C.BaseInterval = TimeSpan.FromMilliseconds(1);
            for (int i = 0; i < 25; i++) Assert.False(await b.C.MoveToLevelWithOnOff(1, 128, 0, CancellationToken.None));
            Assert.InRange(b.Log.Count, 1, 8);
        }

        [Fact]
        public async Task SlowSigma2_WaitsSigma2Timeout_NotResponseTimeout()
        {
            var b = new FakeBulb(new CaseResponder()) { Sigma2DelayMs = 500 };   // ResponseTimeout is 200 ms
            Assert.True(await b.C.MoveToLevelWithOnOff(1, 128, 0, CancellationToken.None));

            b = new FakeBulb(new CaseResponder()) { Sigma2DelayMs = 500 };
            b.C.Sigma2Timeout = TimeSpan.FromMilliseconds(100);
            Assert.False(await b.C.MoveToLevelWithOnOff(1, 128, 0, CancellationToken.None));
        }

        [Fact]
        public async Task SessionLost_NextInvokeReconnects()
        {
            var b = new FakeBulb();
            b.Drop = _ => true;
            Assert.False(await b.C.MoveToLevelWithOnOff(1, 128, 0, CancellationToken.None));
            b.Received.Clear();
            Assert.False(await b.C.MoveToLevelWithOnOff(1, 128, 0, CancellationToken.None)); // nobody answers the Sigma1
            var first = b.Received.First();
            Assert.Equal(0, first.Header.SessionId);
            Assert.Equal(0, first.Proto.ProtocolId);
            Assert.Equal(0x30, first.Proto.Opcode);
            Assert.NotNull(first.Header.SourceNodeId); // CHIP drops unsecured initiator messages without one
        }

        [Fact]
        public async Task DuplicateSigma2_DuringSigma3_IsIgnored()
        {
            var b = new FakeBulb(new CaseResponder()) { ResendSigma2BeforeStatus = true };
            Assert.True(await b.C.MoveToHueAndSaturation(1, 200, 254, 20, CancellationToken.None));
            var sigma3 = b.Received.Single(s => s.Header.SessionId == 0 && s.Proto.Opcode == 0x32);
            Assert.NotNull(sigma3.Proto.AckCounter); // Sigma3 piggybacks the Sigma2 ack
            Assert.Single(b.Invokes);
        }

        private static byte[] Sigma2For(CaseInitiator ci, CaseResponder r) => r.Sigma2(ci.Sigma1());

        [Fact]
        public void Case_Sigma2_AcceptsValidPeer()
        {
            var ci = new CaseInitiator(Creds, 1, 0x1111);
            Assert.NotEmpty(ci.Sigma3(Sigma2For(ci, new CaseResponder())));
        }

        [Fact]
        public void Case_Sigma2_RejectsBadSignature()
        {
            var ci = new CaseInitiator(Creds, 1, 0x1111);
            Assert.Throws<CaseException>(() => ci.Sigma3(Sigma2For(ci, new CaseResponder { FlipSignature = true })));
        }

        [Fact]
        public void Case_Sigma2_RejectsWrongNodeId()
        {
            var ci = new CaseInitiator(Creds, 1, 0x1111);
            Assert.Throws<CaseException>(() => ci.Sigma3(Sigma2For(ci, new CaseResponder { NocNode = 2 })));
        }

        [Fact]
        public void Case_Sigma2_RejectsWrongFabricId()
        {
            var ci = new CaseInitiator(Creds, 1, 0x1111);
            Assert.Throws<CaseException>(() => ci.Sigma3(Sigma2For(ci, new CaseResponder { NocFabric = Creds.FabricId + 1 })));
        }

        [Fact]
        public async Task Invoke_EncodesCommandPath()
        {
            var b = new FakeBulb();
            Assert.True(await b.C.MoveToHueAndSaturation(1, 200, 254, 20, CancellationToken.None));
            var s = b.Invokes.Single();
            Assert.True(s.Proto.Initiator && s.Proto.NeedsAck);

            var r = new TlvReader(s.Body);
            Assert.True(r.Next()); r.EnterContainer();
            var path = new List<ulong>();
            var fields = new List<ulong>();
            ulong? imRev = null;
            while (r.Next())
            {
                if (r.Tag == 2)
                {
                    r.EnterContainer();
                    Assert.True(r.Next()); r.EnterContainer();             // CommandDataIB
                    Assert.True(r.Next()); Assert.Equal(0, r.Tag); r.EnterContainer();
                    while (r.Next()) path.Add(r.GetUInt());
                    r.ExitContainer();
                    Assert.True(r.Next()); Assert.Equal(1, r.Tag); r.EnterContainer();
                    while (r.Next()) fields.Add(r.GetUInt());
                    r.ExitContainer();
                    r.ExitContainer();
                    Assert.False(r.Next()); r.ExitContainer();
                }
                else if (r.Tag == 255) imRev = r.GetUInt();
            }
            Assert.Equal(new ulong[] { 1, 0x0300, 0x06 }, path);
            Assert.Equal(new ulong[] { 200, 254, 20, 0, 0 }, fields);
            Assert.Equal(11UL, imRev);
        }
    }
}
