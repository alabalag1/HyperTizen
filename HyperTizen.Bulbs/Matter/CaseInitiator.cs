using System;
using System.Net;
using System.Text;
using System.Threading;

namespace HyperTizen.Bulbs.Matter
{
    /// <summary>An established CASE session with one bulb.</summary>
    internal sealed class SecureSession
    {
        public readonly ushort LocalId, PeerId;
        public readonly ulong PeerNodeId;
        public readonly byte[] EncryptKey, DecryptKey; // I2R, R2I: we are always the initiator
        public readonly IPEndPoint Endpoint;
        private int _counter;

        public SecureSession(ushort localId, ushort peerId, ulong peerNodeId, byte[] encryptKey, byte[] decryptKey, IPEndPoint endpoint)
        {
            LocalId = localId; PeerId = peerId; PeerNodeId = peerNodeId;
            EncryptKey = encryptKey; DecryptKey = decryptKey; Endpoint = endpoint;
            _counter = (int)(BitConverter.ToUInt32(SessionCrypto.RandomBytes(4), 0) & 0x0fffffff); // Core spec §4.6.1.1
        }

        public uint NextCounter() => (uint)Interlocked.Increment(ref _counter);
    }

    internal sealed class CaseException : Exception
    {
        public CaseException(string message) : base(message) { }
    }

    /// <summary>CASE Sigma1/2/3 initiator (Core spec §4.14.2), following matter.js CaseClient. No resumption.
    /// Message payloads here are the TLV application payloads only; the transcript hashes cover exactly those.</summary>
    internal sealed class CaseInitiator
    {
        public const byte OpSigma1 = 0x30, OpSigma2 = 0x31, OpSigma3 = 0x32, OpSigma2Resume = 0x33;

        private static readonly byte[] InfoSigma2 = Ascii("Sigma2"), InfoSigma3 = Ascii("Sigma3"), InfoSession = Ascii("SessionKeys");
        private static readonly byte[] NonceSigma2 = Ascii("NCASE_Sigma2N"), NonceSigma3 = Ascii("NCASE_Sigma3N");
        private const int Anon = TlvWriter.Anonymous;

        private readonly MatterCredentials _c;
        private readonly ulong _peerNodeId;
        private readonly ushort _localSessionId;
        private readonly byte[] _ephPriv, _ephPub;
        private byte[] _sigma1, _sigma2, _sigma3, _shared;
        private ushort _peerSessionId;

        public CaseInitiator(MatterCredentials c, ulong peerNodeId, ushort localSessionId)
        {
            if (c.RootPublicKey.Length != 65) throw new CaseException("root public key must be 65 bytes");
            _c = c; _peerNodeId = peerNodeId; _localSessionId = localSessionId;
            (_ephPriv, _ephPub) = SessionCrypto.GenerateP256();
        }

        public byte[] Sigma1()
        {
            var random = SessionCrypto.RandomBytes(32);
            var w = new TlvWriter();
            w.StartStructure(Anon);
            w.Put(1, random);
            w.Put(2, (ulong)_localSessionId);
            w.Put(3, DestinationId(_c, _peerNodeId, random));
            w.Put(4, _ephPub);
            w.EndContainer();
            return _sigma1 = w.ToArray();
        }

        /// <summary>destinationId = HMAC-SHA256(IPK, initiatorRandom || rootPublicKey || fabricId (LE64) || nodeId (LE64)), §4.14.2.4.</summary>
        internal static byte[] DestinationId(MatterCredentials c, ulong peerNodeId, byte[] random)
        {
            var m = new byte[32 + 65 + 16];
            Buffer.BlockCopy(random, 0, m, 0, 32);
            Buffer.BlockCopy(c.RootPublicKey, 0, m, 32, 65);
            Bin.Put64(m, 97, c.FabricId);
            Bin.Put64(m, 105, peerNodeId);
            return SessionCrypto.HmacSha256(c.Ipk, m);
        }

        /// <summary>Checks Sigma2 and returns the Sigma3 payload.</summary>
        /// <exception cref="CaseException">bad Sigma2: wrong peer, bad signature, or it cannot be decrypted</exception>
        public byte[] Sigma3(byte[] sigma2)
        {
            _sigma2 = sigma2;
            byte[] responderRandom = null, peerEph = null, encrypted = null;
            ulong? peerSession = null;
            var r = new TlvReader(sigma2);
            if (!r.Next() || r.Type != TlvType.Structure) throw new CaseException("Sigma2 is not a structure");
            r.EnterContainer();
            while (r.Next())
            {
                switch (r.Tag)
                {
                    case 1: responderRandom = r.GetBytes(); break;
                    case 2: peerSession = r.GetUInt(); break;
                    case 3: peerEph = r.GetBytes(); break;
                    case 4: encrypted = r.GetBytes(); break;
                }
            }
            if (responderRandom == null || peerSession == null || peerEph == null || encrypted == null || peerSession > ushort.MaxValue)
                throw new CaseException("Sigma2 is missing fields");
            _peerSessionId = (ushort)peerSession.Value;
            _shared = SessionCrypto.EcdhP256(_ephPriv, peerEph);

            var sigma2Key = SessionCrypto.Hkdf(_shared, Concat(_c.Ipk, responderRandom, peerEph, SessionCrypto.Sha256(_sigma1)), InfoSigma2, 16);
            byte[] tbe;
            try { tbe = MessageCodec.Ccm(false, sigma2Key, NonceSigma2, null, encrypted); }
            catch (Org.BouncyCastle.Crypto.InvalidCipherTextException) { throw new CaseException("Sigma2 encrypted data MIC check failed"); }

            byte[] noc = null, icac = null, sig = null;
            r = new TlvReader(tbe);
            if (!r.Next() || r.Type != TlvType.Structure) throw new CaseException("Sigma2 TBEData is not a structure");
            r.EnterContainer();
            while (r.Next())
            {
                switch (r.Tag)
                {
                    case 1: noc = r.GetBytes(); break;
                    case 2: icac = r.GetBytes(); break;
                    case 3: sig = r.GetBytes(); break;
                }
            }
            if (noc == null || sig == null) throw new CaseException("Sigma2 TBEData is missing fields");

            // ponytail: trusts the responder NOC without checking its chain to RootCert (that needs a Matter TLV -> X.509 DER
            // rebuild). Only fabric members can answer at all, since the Sigma2 key is IPK-derived, but any member of this
            // fabric could impersonate a bulb. Upgrade: rebuild the NOC (and ICAC) as DER and verify their signatures up to RootCert.
            ReadNoc(noc, out var nocKey, out var nocNode, out var nocFabric);
            if (nocNode != _peerNodeId) throw new CaseException("peer NOC node id " + nocNode + " is not the expected " + _peerNodeId);
            if (nocFabric != _c.FabricId) throw new CaseException("peer NOC is for another fabric");
            if (!SessionCrypto.VerifyP256(nocKey, SignedData(noc, icac, peerEph, _ephPub), sig))
                throw new CaseException("Sigma2 signature does not verify");

            var sigma3Key = SessionCrypto.Hkdf(_shared, Concat(_c.Ipk, SessionCrypto.Sha256(Concat(_sigma1, _sigma2))), InfoSigma3, 16);
            var mySig = SessionCrypto.SignP256(_c.PrivateKey, SignedData(_c.OperationalCert, null, _ephPub, peerEph));
            var w = new TlvWriter();
            w.StartStructure(Anon);
            w.Put(1, _c.OperationalCert);
            w.Put(3, mySig);
            w.EndContainer();
            var enc = MessageCodec.Ccm(true, sigma3Key, NonceSigma3, null, w.ToArray());
            w = new TlvWriter();
            w.StartStructure(Anon);
            w.Put(1, enc);
            w.EndContainer();
            return _sigma3 = w.ToArray();
        }

        /// <summary>Call after the responder's SUCCESS status report for Sigma3.</summary>
        public SecureSession Finish(IPEndPoint ep)
        {
            var keys = SessionCrypto.Hkdf(_shared, Concat(_c.Ipk, SessionCrypto.Sha256(Concat(_sigma1, _sigma2, _sigma3))), InfoSession, 48);
            var i2r = new byte[16];
            var r2i = new byte[16];
            Buffer.BlockCopy(keys, 0, i2r, 0, 16);
            Buffer.BlockCopy(keys, 16, r2i, 0, 16);
            return new SecureSession(_localSessionId, _peerSessionId, _peerNodeId, i2r, r2i, ep);
        }

        /// <summary>TBSData (§4.14.2.3): { 1: NOC, 2: ICAC (optional), 3: sender ephemeral key, 4: receiver ephemeral key }.</summary>
        private static byte[] SignedData(byte[] noc, byte[] icac, byte[] senderPub, byte[] receiverPub)
        {
            var w = new TlvWriter();
            w.StartStructure(Anon);
            w.Put(1, noc);
            if (icac != null) w.Put(2, icac);
            w.Put(3, senderPub);
            w.Put(4, receiverPub);
            w.EndContainer();
            return w.ToArray();
        }

        /// <summary>Matter TLV certificate: tag 6 subject DN list (17 node id, 21 fabric id), tag 9 public key.</summary>
        private static void ReadNoc(byte[] noc, out byte[] publicKey, out ulong nodeId, out ulong fabricId)
        {
            publicKey = null;
            ulong? node = null, fabric = null;
            var r = new TlvReader(noc);
            if (!r.Next() || r.Type != TlvType.Structure) throw new CaseException("peer NOC is not a structure");
            r.EnterContainer();
            while (r.Next())
            {
                if (r.Tag == 9) publicKey = r.GetBytes();
                else if (r.Tag == 6 && r.Type == TlvType.List)
                {
                    r.EnterContainer();
                    while (r.Next())
                    {
                        if (r.Tag == 17) node = r.GetUInt();
                        else if (r.Tag == 21) fabric = r.GetUInt();
                    }
                    r.ExitContainer();
                }
            }
            if (publicKey == null || node == null || fabric == null) throw new CaseException("peer NOC is missing fields");
            nodeId = node.Value;
            fabricId = fabric.Value;
        }

        private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

        private static byte[] Concat(params byte[][] parts)
        {
            int n = 0;
            foreach (var p in parts) n += p.Length;
            var r = new byte[n];
            n = 0;
            foreach (var p in parts) { Buffer.BlockCopy(p, 0, r, n, p.Length); n += p.Length; }
            return r;
        }
    }
}
