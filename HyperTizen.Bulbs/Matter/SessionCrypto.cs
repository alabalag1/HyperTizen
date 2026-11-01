using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities;

namespace HyperTizen.Bulbs.Matter
{
    /// <summary>Matter session crypto primitives (Core spec §3) on BouncyCastle. P-256 keys: 32-byte private scalar,
    /// 65-byte uncompressed public point; signatures are raw r||s (64 bytes).</summary>
    internal static class SessionCrypto
    {
        private static readonly ECDomainParameters P256 = new ECDomainParameters(ECNamedCurveTable.GetByName("P-256"));

        public static byte[] Hkdf(byte[] ikm, byte[] salt, byte[] info, int len)
        {
            var g = new HkdfBytesGenerator(new Sha256Digest());
            g.Init(new HkdfParameters(ikm, salt, info));
            var okm = new byte[len];
            g.GenerateBytes(okm, 0, len);
            return okm;
        }

        public static readonly SecureRandom Random = new SecureRandom();

        /// <summary>A fresh P-256 key pair: 32-byte private scalar, 65-byte uncompressed public point.</summary>
        public static (byte[] Priv, byte[] Pub) GenerateP256()
        {
            var g = new ECKeyPairGenerator();
            g.Init(new ECKeyGenerationParameters(P256, Random));
            var kp = g.GenerateKeyPair();
            return (BigIntegers.AsUnsignedByteArray(32, ((ECPrivateKeyParameters)kp.Private).D),
                ((ECPublicKeyParameters)kp.Public).Q.GetEncoded(false));
        }

        public static byte[] RandomBytes(int n)
        {
            var b = new byte[n];
            Random.NextBytes(b);
            return b;
        }

        public static byte[] EcdhP256(byte[] priv, byte[] peerPub)
        {
            var a = new ECDHBasicAgreement();
            a.Init(Private(priv));
            return BigIntegers.AsUnsignedByteArray(32, a.CalculateAgreement(Public(peerPub)));
        }

        /// <summary>ECDSA-SHA256 with deterministic k (RFC 6979), so no RNG is needed.</summary>
        public static byte[] SignP256(byte[] priv, byte[] msg)
        {
            var s = Signer();
            s.Init(true, Private(priv));
            s.BlockUpdate(msg, 0, msg.Length);
            return s.GenerateSignature();
        }

        public static bool VerifyP256(byte[] pub, byte[] msg, byte[] sig)
        {
            var s = Signer();
            s.Init(false, Public(pub));
            s.BlockUpdate(msg, 0, msg.Length);
            return s.VerifySignature(sig);
        }

        public static byte[] Sha256(byte[] data)
        {
            var d = new Sha256Digest();
            d.BlockUpdate(data, 0, data.Length);
            var r = new byte[32];
            d.DoFinal(r, 0);
            return r;
        }

        public static byte[] HmacSha256(byte[] key, byte[] data)
        {
            var h = new HMac(new Sha256Digest());
            h.Init(new KeyParameter(key));
            h.BlockUpdate(data, 0, data.Length);
            var r = new byte[32];
            h.DoFinal(r, 0);
            return r;
        }

        private static DsaDigestSigner Signer() =>
            new DsaDigestSigner(new ECDsaSigner(new HMacDsaKCalculator(new Sha256Digest())), new Sha256Digest(), PlainDsaEncoding.Instance);

        private static ECPrivateKeyParameters Private(byte[] d) => new ECPrivateKeyParameters(new BigInteger(1, d), P256);

        // DecodePoint rejects points that are not on the curve
        private static ECPublicKeyParameters Public(byte[] q) => new ECPublicKeyParameters(P256.Curve.DecodePoint(q), P256);
    }
}
