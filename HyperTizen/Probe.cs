using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tizen.System;

namespace HyperTizen
{
    // Throwaway spike (Task 1): answers "does mDNS over IPv6 work on the TV" and "can we send before standby".
    // Deleted once the results are recorded.
    static class Probe
    {
        const string ProbeHost = "192.168.0.3"; // Mac; hardcoded, this file is deleted later
        const int ProbePort = 5599;

        public static void Run()
        {
            Display.StateChanged += (s, e) =>
            {
                Helper.Log.Write(Helper.eLogType.Info, $"[Probe] Display state -> {e.State}");
                if (e.State == DisplayState.Off) Task.Run(SendDisplayOff);
            };
            Task.Run(MdnsProbe);
            Helper.Log.Write(Helper.eLogType.Info, "[Probe] started");
        }

        static void SendDisplayOff()
        {
            try
            {
                using (var udp = new UdpClient())
                {
                    var ep = new IPEndPoint(IPAddress.Parse(ProbeHost), ProbePort);
                    for (int i = 0; i < 50; i++)
                    {
                        string msg = $"display-off {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
                        try
                        {
                            byte[] b = Encoding.ASCII.GetBytes(msg);
                            int n = udp.Send(b, b.Length, ep);
                            Helper.Log.Write(Helper.eLogType.Info, $"[Probe] send #{i} ok ({n} bytes): {msg}");
                        }
                        catch (Exception ex)
                        {
                            Helper.Log.Write(Helper.eLogType.Error, $"[Probe] send #{i} failed: {ex.Message}");
                        }
                        Thread.Sleep(100);
                    }
                }
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Error, $"[Probe] display-off sender failed: {ex.Message}");
            }
        }

        static void MdnsProbe()
        {
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    string desc = $"{nic.Name} type={nic.NetworkInterfaceType} status={nic.OperationalStatus} mcast={nic.SupportsMulticast} v6={nic.Supports(NetworkInterfaceComponent.IPv6)}";
                    int idx = -1;
                    try { idx = nic.GetIPProperties().GetIPv6Properties().Index; } catch { }
                    Helper.Log.Write(Helper.eLogType.Info, $"[Probe] mDNS NIC {desc} ipv6Index={idx}");
                    if (nic.OperationalStatus != OperationalStatus.Up || !nic.SupportsMulticast || idx < 0) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    try { QueryInterface(nic.Name, idx); }
                    catch (Exception ex) { Helper.Log.Write(Helper.eLogType.Error, $"[Probe] mDNS {nic.Name} failed: {ex.Message}"); }
                }
                Helper.Log.Write(Helper.eLogType.Info, "[Probe] mDNS done");
            }
            catch (Exception ex)
            {
                Helper.Log.Write(Helper.eLogType.Error, $"[Probe] mDNS probe failed: {ex}");
            }
        }

        static void QueryInterface(string name, int ifIndex)
        {
            using (var sock = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp))
            {
                sock.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
                var dest = new IPEndPoint(new IPAddress(IPAddress.Parse("ff02::fb").GetAddressBytes(), ifIndex), 5353);
                var aaaaSeen = new HashSet<string>();
                var srvTargets = new HashSet<string>();
                var asked = new HashSet<string>();

                void Ask(string qname, ushort qtype)
                {
                    sock.SendTo(BuildQuery(qname, qtype), dest);
                    Helper.Log.Write(Helper.eLogType.Info, $"[Probe] mDNS {name}(scope {ifIndex}) query {qname} type {qtype}");
                }

                Ask("_matter._tcp.local", 12);
                var deadline = DateTime.UtcNow.AddSeconds(6);
                int resend = 0;
                var buf = new byte[9000];
                while (DateTime.UtcNow < deadline)
                {
                    if (!sock.Poll(500_000, SelectMode.SelectRead))
                    {
                        if (++resend == 4) Ask("_matter._tcp.local", 12); // one retry at ~2s
                        continue;
                    }
                    EndPoint from = new IPEndPoint(IPAddress.IPv6Any, 0);
                    int n = sock.ReceiveFrom(buf, ref from);
                    var src = (IPEndPoint)from;
                    Helper.Log.Write(Helper.eLogType.Info, $"[Probe] mDNS {name}(scope {ifIndex}) reply from {src.Address} scope={src.Address.ScopeId} ({n} bytes)");
                    foreach (var rr in ParseRecords(buf, n))
                    {
                        Helper.Log.Write(Helper.eLogType.Info, $"[Probe]   {rr}");
                        if (rr.StartsWith("AAAA ")) aaaaSeen.Add(rr.Split(' ')[1]);
                        if (rr.StartsWith("SRV ")) srvTargets.Add(rr.Split(' ')[2]);
                    }
                    // SRV target without a matching AAAA yet: ask for it directly
                    foreach (var t in srvTargets)
                        if (!aaaaSeen.Contains(t) && asked.Add(t)) Ask(t, 28);
                }
            }
        }

        static byte[] BuildQuery(string qname, ushort qtype)
        {
            var m = new List<byte> { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
            foreach (var label in qname.Split('.'))
            {
                m.Add((byte)label.Length);
                m.AddRange(Encoding.ASCII.GetBytes(label));
            }
            m.Add(0);
            m.Add((byte)(qtype >> 8)); m.Add((byte)qtype);
            m.Add(0); m.Add(1); // class IN, QU bit clear (we are not on 5353 so replies are unicast anyway)
            return m.ToArray();
        }

        static IEnumerable<string> ParseRecords(byte[] b, int len)
        {
            var res = new List<string>();
            try
            {
                int qd = (b[4] << 8) | b[5];
                int rrs = ((b[6] << 8) | b[7]) + ((b[8] << 8) | b[9]) + ((b[10] << 8) | b[11]);
                int p = 12;
                for (int i = 0; i < qd; i++) { ReadName(b, ref p); p += 4; }
                for (int i = 0; i < rrs && p < len; i++)
                {
                    string name = ReadName(b, ref p);
                    int type = (b[p] << 8) | b[p + 1];
                    int rdlen = (b[p + 8] << 8) | b[p + 9];
                    int rd = p + 10;
                    switch (type)
                    {
                        case 12: { int q = rd; res.Add($"PTR {name} -> {ReadName(b, ref q)}"); break; }
                        case 33: { int q = rd + 6; res.Add($"SRV {name} {ReadName(b, ref q)} port={(b[rd + 4] << 8) | b[rd + 5]}"); break; }
                        case 28: { var a = new byte[16]; Array.Copy(b, rd, a, 0, 16); res.Add($"AAAA {name} {new IPAddress(a)}"); break; }
                        case 1: res.Add($"A {name} {b[rd]}.{b[rd + 1]}.{b[rd + 2]}.{b[rd + 3]}"); break;
                        default: res.Add($"type{type} {name} ({rdlen} bytes)"); break;
                    }
                    p = rd + rdlen;
                }
            }
            catch (Exception ex) { res.Add($"parse error: {ex.Message}"); }
            return res;
        }

        static string ReadName(byte[] b, ref int p)
        {
            var sb = new StringBuilder();
            int pos = p; bool jumped = false;
            while (true)
            {
                int l = b[pos];
                if (l == 0) { pos++; break; }
                if ((l & 0xC0) == 0xC0)
                {
                    int ptr = ((l & 0x3F) << 8) | b[pos + 1];
                    if (!jumped) p = pos + 2;
                    jumped = true; pos = ptr; continue;
                }
                sb.Append(Encoding.UTF8.GetString(b, pos + 1, l)).Append('.');
                pos += 1 + l;
            }
            if (!jumped) p = pos;
            return sb.ToString().TrimEnd('.');
        }
    }
}
