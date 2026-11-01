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

        /// <summary>Answers InvokeRequests with SUCCESS on the installed session; <see cref="Drop"/> loses datagrams.</summary>
        private sealed class FakeBulb
        {
            public readonly byte[] I2R = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
            public readonly byte[] R2I = Enumerable.Range(16, 16).Select(i => (byte)i).ToArray();
            public const ushort CtrlSession = 0x1111, BulbSession = 0x2222;
            public const ulong Node = 1;
            public readonly List<Sent> Received = new List<Sent>();
            public Func<Sent, bool> Drop = _ => false;
            public MatterController C;
            private uint _counter = 500;

            public FakeBulb()
            {
                C = new MatterController(Creds, Send, _ => { }) { BaseInterval = TimeSpan.FromMilliseconds(10), ResponseTimeout = TimeSpan.FromMilliseconds(200) };
                C.InstallSession(Node, new SecureSession(CtrlSession, BulbSession, Node, I2R, R2I, new IPEndPoint(IPAddress.IPv6Loopback, 5540)));
            }

            public IEnumerable<Sent> Invokes => Received.Where(s => s.Proto.ProtocolId == 1 && s.Proto.Opcode == 0x08);

            private Task Send(byte[] d, IPEndPoint ep)
            {
                var s = new Sent();
                byte[] plain = Bin.Get16(d, 1) == 0
                    ? MessageCodec.DecodeUnsecured(d, out s.Header)
                    : MessageCodec.DecodeSecured(d, I2R, Creds.NodeId, out s.Header);
                s.Proto = ProtocolHeader.Decode(plain, out int off);
                s.Body = plain.Skip(off).ToArray();
                lock (Received) Received.Add(s);
                if (s.Header.SessionId != BulbSession || Drop(s) || s.Proto.Opcode != 0x08) return Task.CompletedTask;

                // InvokeResponse { 0: false, 1: [ { 1: CommandStatusIB { 0: path, 1: { 0: SUCCESS } } } ], 255: 11 }
                var w = new TlvWriter();
                w.StartStructure(TlvWriter.Anonymous);
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
