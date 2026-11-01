using System;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace HyperTizen.Bulbs.Matter
{
    /// <summary>Matter message header (Core spec §4.4.1), privacy off.</summary>
    internal struct MessageHeader
    {
        public ushort SessionId;
        public byte SecurityFlags;
        public uint Counter;
        public ulong? SourceNodeId, DestNodeId;
        public ushort? DestGroupId;
    }

    /// <summary>Protocol (payload) header (Core spec §4.4.3): the cleartext start of the encrypted payload.</summary>
    internal struct ProtocolHeader
    {
        public bool Initiator, NeedsAck;
        public byte Opcode;
        public ushort ExchangeId, ProtocolId;
        public uint? AckCounter;

        private const byte FlagInitiator = 0x01, FlagAck = 0x02, FlagReliable = 0x04, FlagSecuredExt = 0x08, FlagVendor = 0x10;

        public byte[] Encode()
        {
            var b = new byte[AckCounter.HasValue ? 10 : 6];
            b[0] = (byte)((Initiator ? FlagInitiator : 0) | (AckCounter.HasValue ? FlagAck : 0) | (NeedsAck ? FlagReliable : 0));
            b[1] = Opcode;
            Bin.Put16(b, 2, ExchangeId);
            Bin.Put16(b, 4, ProtocolId);
            if (AckCounter.HasValue) Bin.Put32(b, 6, AckCounter.Value);
            return b;
        }

        /// <param name="bodyOffset">index of the application payload (e.g. the IM TLV) in <paramref name="payload"/></param>
        public static ProtocolHeader Decode(byte[] payload, out int bodyOffset)
        {
            if (payload.Length < 6) throw new FormatException("protocol header truncated");
            byte f = payload[0];
            if ((f & FlagVendor) != 0) throw new NotSupportedException("vendor-specific protocol id");
            var h = new ProtocolHeader
            {
                Initiator = (f & FlagInitiator) != 0,
                NeedsAck = (f & FlagReliable) != 0,
                Opcode = payload[1],
                ExchangeId = Bin.Get16(payload, 2),
                ProtocolId = Bin.Get16(payload, 4),
            };
            int p = 6;
            if ((f & FlagAck) != 0)
            {
                if (payload.Length < p + 4) throw new FormatException("protocol header truncated");
                h.AckCounter = Bin.Get32(payload, p);
                p += 4;
            }
            if ((f & FlagSecuredExt) != 0) // skipped, as matter.js does
            {
                if (payload.Length < p + 2) throw new FormatException("protocol header truncated");
                p += 2 + Bin.Get16(payload, p);
                if (p > payload.Length) throw new FormatException("secured extension overruns payload");
            }
            bodyOffset = p;
            return h;
        }
    }

    /// <summary>Secured unicast message framing: AES-128-CCM with a 16-byte MIC, the header as AAD (Core spec §4.7, §4.8).</summary>
    internal static class MessageCodec
    {
        private const int MicLength = 16;
        private const byte FlagSourceNode = 0x04, DsizMask = 0x03;
        private const byte SecPrivacy = 0x80, SecControl = 0x40, SecExtensions = 0x20, SessionTypeMask = 0x03;

        /// <param name="sourceNodeId">the sender's operational node id; it goes into the nonce only, not the header</param>
        public static byte[] EncodeSecured(ushort sessionId, uint counter, byte[] payloadHeaderAndBody, byte[] key, ulong sourceNodeId)
        {
            var header = new byte[8]; // flags 0 (version 0, no node ids), session id, security flags 0 (unicast), counter
            Bin.Put16(header, 1, sessionId);
            Bin.Put32(header, 4, counter);
            var ct = Ccm(true, key, Nonce(header[3], counter, sourceNodeId), header, payloadHeaderAndBody);
            var d = new byte[header.Length + ct.Length];
            Buffer.BlockCopy(header, 0, d, 0, header.Length);
            Buffer.BlockCopy(ct, 0, d, header.Length, ct.Length);
            return d;
        }

        /// <param name="sourceNodeId">the sender's operational node id (the peer's), needed for the nonce because
        /// unicast headers do not carry it</param>
        /// <exception cref="Org.BouncyCastle.Crypto.InvalidCipherTextException">MIC check failed</exception>
        public static byte[] DecodeSecured(byte[] datagram, byte[] key, ulong sourceNodeId, out MessageHeader header)
        {
            int p = DecodeHeader(datagram, out header);
            if (datagram.Length < p + MicLength) throw new FormatException("message too short");
            var aad = new byte[p];
            Buffer.BlockCopy(datagram, 0, aad, 0, p);
            var ct = new byte[datagram.Length - p];
            Buffer.BlockCopy(datagram, p, ct, 0, ct.Length);
            return Ccm(false, key, Nonce(header.SecurityFlags, header.Counter, sourceNodeId), aad, ct);
        }

        /// <summary>Unsecured unicast message (session id 0, used by CASE). The initiator must send its ephemeral node id
        /// as the source; the responder answers with it as the destination.</summary>
        public static byte[] EncodeUnsecured(uint counter, byte[] payloadHeaderAndBody, ulong? sourceNodeId, ulong? destNodeId)
        {
            int n = 8 + (sourceNodeId.HasValue ? 8 : 0) + (destNodeId.HasValue ? 8 : 0);
            var d = new byte[n + payloadHeaderAndBody.Length];
            d[0] = (byte)((sourceNodeId.HasValue ? FlagSourceNode : 0) | (destNodeId.HasValue ? 1 : 0));
            Bin.Put32(d, 4, counter);
            int p = 8;
            if (sourceNodeId.HasValue) { Bin.Put64(d, p, sourceNodeId.Value); p += 8; }
            if (destNodeId.HasValue) { Bin.Put64(d, p, destNodeId.Value); p += 8; }
            Buffer.BlockCopy(payloadHeaderAndBody, 0, d, p, payloadHeaderAndBody.Length);
            return d;
        }

        public static byte[] DecodeUnsecured(byte[] datagram, out MessageHeader header)
        {
            int p = DecodeHeader(datagram, out header);
            if (header.SessionId != 0) throw new FormatException("not an unsecured message");
            var r = new byte[datagram.Length - p];
            Buffer.BlockCopy(datagram, p, r, 0, r.Length);
            return r;
        }

        /// <returns>the length of the message header (the AAD of a secured message)</returns>
        private static int DecodeHeader(byte[] datagram, out MessageHeader header)
        {
            if (datagram.Length < 8) throw new FormatException("message too short");
            byte flags = datagram[0];
            if (flags >> 4 != 0) throw new NotSupportedException("message version " + (flags >> 4));
            header = new MessageHeader
            {
                SessionId = Bin.Get16(datagram, 1),
                SecurityFlags = datagram[3],
                Counter = Bin.Get32(datagram, 4),
            };
            if ((header.SecurityFlags & (SecPrivacy | SecControl | SecExtensions)) != 0)
                throw new NotSupportedException("privacy, control or extension messages");
            if ((header.SecurityFlags & SessionTypeMask) != 0) throw new NotSupportedException("group session");

            int p = 8;
            int need = p + ((flags & FlagSourceNode) != 0 ? 8 : 0) + ((flags & DsizMask) == 1 ? 8 : (flags & DsizMask) == 2 ? 2 : 0);
            if (datagram.Length < need) throw new FormatException("message too short");
            if ((flags & FlagSourceNode) != 0) { header.SourceNodeId = Bin.Get64(datagram, p); p += 8; }
            switch (flags & DsizMask)
            {
                case 1: header.DestNodeId = Bin.Get64(datagram, p); p += 8; break;
                case 2: header.DestGroupId = Bin.Get16(datagram, p); p += 2; break;
                case 3: throw new FormatException("reserved DSIZ");
            }
            return p;
        }

        /// <summary>Core spec §4.8.1: security flags || message counter (LE) || source node id (LE), 13 bytes.</summary>
        private static byte[] Nonce(byte securityFlags, uint counter, ulong sourceNodeId)
        {
            var n = new byte[13];
            n[0] = securityFlags;
            Bin.Put32(n, 1, counter);
            Bin.Put64(n, 5, sourceNodeId);
            return n;
        }

        /// <summary>AES-128-CCM, 16-byte MIC. Also used bare for the CASE Sigma2/Sigma3 encrypted payloads.</summary>
        internal static byte[] Ccm(bool encrypt, byte[] key, byte[] nonce, byte[] aad, byte[] data)
        {
            var c = new CcmBlockCipher(new AesEngine());
            c.Init(encrypt, new AeadParameters(new KeyParameter(key), MicLength * 8, nonce, aad));
            return c.ProcessPacket(data, 0, data.Length);
        }
    }

    internal static class Bin
    {
        public static void Put16(byte[] b, int o, ushort v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
        public static void Put32(byte[] b, int o, uint v) { for (int i = 0; i < 4; i++) b[o + i] = (byte)(v >> (8 * i)); }
        public static void Put64(byte[] b, int o, ulong v) { for (int i = 0; i < 8; i++) b[o + i] = (byte)(v >> (8 * i)); }
        public static ushort Get16(byte[] b, int o) => (ushort)(b[o] | b[o + 1] << 8);
        public static uint Get32(byte[] b, int o) => (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24);
        public static ulong Get64(byte[] b, int o) => Get32(b, o) | (ulong)Get32(b, o + 4) << 32;
    }
}
