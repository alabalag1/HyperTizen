using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace HyperTizen.Bulbs
{
    public sealed record BulbEntry(ulong NodeId, string Zone, string[] Addresses);

    public sealed class CredentialsException : Exception
    {
        public CredentialsException(string message, Exception inner = null) : base(message, inner) { }
    }

    /// <summary>Pairing credentials exported from the matter.js controller (see export-credentials.mjs).</summary>
    public sealed class MatterCredentials
    {
        public ulong FabricId, NodeId;
        public byte[] RootPublicKey, RootCert, OperationalCert, PrivateKey, Ipk;
        public IReadOnlyList<BulbEntry> Bulbs;

        public static MatterCredentials Load(string path)
        {
            try
            {
                var j = JObject.Parse(File.ReadAllText(path));
                var c = new MatterCredentials
                {
                    FabricId = ulong.Parse(Str(j, "fabricId")),
                    NodeId = ulong.Parse(Str(j, "nodeId")),
                    RootPublicKey = Bytes(j, "rootPublicKey"),
                    RootCert = Bytes(j, "rootCert"),
                    OperationalCert = Bytes(j, "operationalCert"),
                    PrivateKey = Bytes(j, "privateKey", 32),
                    Ipk = Bytes(j, "ipk", 16),
                    Bulbs = ((JArray)j["bulbs"] ?? throw new CredentialsException("missing bulbs"))
                        .Select(b => new BulbEntry(ulong.Parse(Str(b, "nodeId")), Str(b, "zone"),
                            ((JArray)b["addresses"] ?? throw new CredentialsException("missing addresses"))
                                .Select(a => (string)a).ToArray()))
                        .ToList(),
                };
                return c;
            }
            catch (CredentialsException) { throw; }
            catch (Exception e)
            {
                // never include file contents in the message: it holds the private key
                throw new CredentialsException("cannot load credentials from " + path + ": " + e.GetType().Name, e);
            }
        }

        private static string Str(JToken j, string name) =>
            (string)j[name] ?? throw new CredentialsException("missing " + name);

        private static byte[] Bytes(JToken j, string name, int? length = null)
        {
            var b = Convert.FromBase64String(Str(j, name));
            if (length.HasValue && b.Length != length.Value)
                throw new CredentialsException(name + " must be " + length + " bytes, got " + b.Length);
            return b;
        }
    }
}
