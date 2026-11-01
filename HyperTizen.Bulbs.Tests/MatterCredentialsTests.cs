using System;
using System.IO;
using HyperTizen.Bulbs;
using Xunit;

namespace HyperTizen.Bulbs.Tests
{
    public class MatterCredentialsTests
    {
        private static string Fixture => Path.Combine(AppContext.BaseDirectory, "fixtures", "credentials-fake.json");

        [Fact]
        public void Load_ParsesFakeFile()
        {
            var c = MatterCredentials.Load(Fixture);
            Assert.Equal(1234567890123456789UL, c.FabricId);
            Assert.Equal(ulong.MaxValue, c.NodeId);
            Assert.Equal(65, c.RootPublicKey.Length);
            Assert.Equal(231, c.RootCert.Length);
            Assert.Equal(255, c.OperationalCert.Length);
            Assert.Equal(32, c.PrivateKey.Length);
            Assert.Equal(16, c.Ipk.Length);
            Assert.Equal(2, c.Bulbs.Count);
            Assert.Equal("upper", c.Bulbs[0].Zone);
            Assert.Equal(2UL, c.Bulbs[1].NodeId);
            Assert.Equal(new[] { "fe80::5:6:7:8" }, c.Bulbs[1].Addresses);
        }

        private static string Temp(string json)
        {
            var p = Path.GetTempFileName();
            File.WriteAllText(p, json);
            return p;
        }

        [Fact]
        public void Load_MissingOrCorrupt_Throws_CredentialsException()
        {
            Assert.Throws<CredentialsException>(() => MatterCredentials.Load("/no/such/bulbs.json"));
            Assert.Throws<CredentialsException>(() => MatterCredentials.Load(Temp("{ not json")));

            var good = File.ReadAllText(Fixture);
            var noKey = Newtonsoft.Json.Linq.JObject.Parse(good);
            noKey.Remove("privateKey");
            Assert.Throws<CredentialsException>(() => MatterCredentials.Load(Temp(noKey.ToString())));

            var shortKey = Newtonsoft.Json.Linq.JObject.Parse(good);
            shortKey["privateKey"] = Convert.ToBase64String(new byte[31]);
            Assert.Throws<CredentialsException>(() => MatterCredentials.Load(Temp(shortKey.ToString())));
        }
    }
}
