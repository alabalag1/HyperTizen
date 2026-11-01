using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HyperTizen.Bulbs.Matter
{
    internal enum TlvType { SignedInt, UnsignedInt, Bool, Utf8, Bytes, Null, Structure, Array, List }

    /// <summary>Matter TLV (Core spec appendix A), anonymous and context tags only. Integers and lengths use the minimal width.</summary>
    internal sealed class TlvWriter
    {
        public const int Anonymous = -1;
        private readonly MemoryStream _b = new MemoryStream();

        public void StartStructure(int tag) => Control(tag, 0x15);
        public void StartArray(int tag) => Control(tag, 0x16);
        public void StartList(int tag) => Control(tag, 0x17);
        public void EndContainer() => _b.WriteByte(0x18);

        public void Put(int tag, ulong v)
        {
            int w = Width(v);
            Control(tag, 0x04 | w);
            Le(v, 1 << w);
        }

        public void Put(int tag, long v)
        {
            int w = v >= sbyte.MinValue && v <= sbyte.MaxValue ? 0
                : v >= short.MinValue && v <= short.MaxValue ? 1
                : v >= int.MinValue && v <= int.MaxValue ? 2 : 3;
            Control(tag, w);
            Le((ulong)v, 1 << w);
        }

        public void Put(int tag, bool v) => Control(tag, v ? 0x09 : 0x08);
        public void Put(int tag, byte[] v) => Str(tag, 0x10, v);
        public void Put(int tag, string v) => Str(tag, 0x0c, Encoding.UTF8.GetBytes(v));
        public byte[] ToArray() => _b.ToArray();

        private void Str(int tag, int type, byte[] v)
        {
            int w = Width((ulong)v.Length);
            Control(tag, type | w);
            Le((ulong)v.Length, 1 << w);
            _b.Write(v, 0, v.Length);
        }

        private void Control(int tag, int type)
        {
            if (tag == Anonymous) { _b.WriteByte((byte)type); return; }
            if (tag < 0 || tag > 255) throw new ArgumentOutOfRangeException(nameof(tag));
            _b.WriteByte((byte)(0x20 | type));
            _b.WriteByte((byte)tag);
        }

        private static int Width(ulong v) => v <= 0xff ? 0 : v <= 0xffff ? 1 : v <= 0xffffffff ? 2 : 3;

        private void Le(ulong v, int n)
        {
            for (int i = 0; i < n; i++) _b.WriteByte((byte)(v >> (8 * i)));
        }
    }

    /// <summary>Cursor over Matter TLV. Next() moves to the next sibling; it returns false at the end of the current container.</summary>
    internal sealed class TlvReader
    {
        private readonly byte[] _d;
        private int _pos, _valStart, _valLen, _depth;
        private ulong _int;
        private bool _unentered; // current element is a container whose body has not been entered or skipped

        public TlvReader(byte[] data) => _d = data;

        public int Tag { get; private set; }
        public TlvType Type { get; private set; }

        public bool Next()
        {
            if (_unentered) { EnterContainer(); ExitContainer(); }
            if (_pos >= _d.Length || _d[_pos] == 0x18) return false;

            int c = _d[_pos++];
            switch (c >> 5)
            {
                case 0: Tag = TlvWriter.Anonymous; break;
                case 1: Tag = (int)Le(1); break;
                default: throw new FormatException("unsupported TLV tag form 0x" + c.ToString("x2"));
            }
            int t = c & 0x1f;
            if (t <= 0x07)
            {
                Type = t < 4 ? TlvType.SignedInt : TlvType.UnsignedInt;
                _int = Le(1 << (t & 3));
            }
            else if (t == 0x08 || t == 0x09) Type = TlvType.Bool;
            else if (t >= 0x0c && t <= 0x13)
            {
                Type = t < 0x10 ? TlvType.Utf8 : TlvType.Bytes;
                ulong len = Le(1 << (t & 3));
                if (len > (ulong)(_d.Length - _pos)) throw new FormatException("TLV string overruns buffer");
                _valStart = _pos; _valLen = (int)len; _pos += _valLen;
            }
            else if (t == 0x14) Type = TlvType.Null;
            else if (t >= 0x15 && t <= 0x17) { Type = TlvType.Structure + (t - 0x15); _unentered = true; }
            // ponytail: no floats, no signed/bool getters; the Matter clusters and CASE messages we use carry neither
            else throw new FormatException("unsupported TLV element type 0x" + t.ToString("x2"));
            return true;
        }

        public ulong GetUInt() => Type == TlvType.UnsignedInt ? _int : throw new InvalidOperationException("not an unsigned int: " + Type);

        public byte[] GetBytes()
        {
            if (Type != TlvType.Bytes && Type != TlvType.Utf8) throw new InvalidOperationException("not a byte/utf8 string: " + Type);
            var r = new byte[_valLen];
            Buffer.BlockCopy(_d, _valStart, r, 0, _valLen);
            return r;
        }

        public void EnterContainer()
        {
            if (!_unentered) throw new InvalidOperationException("current element is not an unentered container");
            _unentered = false;
            _depth++;
        }

        /// <summary>Skips whatever is left of the current container and steps past its end marker.</summary>
        public void ExitContainer()
        {
            if (_depth == 0) throw new InvalidOperationException("not inside a container");
            while (Next()) { }
            if (_pos >= _d.Length) throw new FormatException("TLV container not terminated");
            _pos++;
            _depth--;
        }

        private ulong Le(int n)
        {
            if (n > _d.Length - _pos) throw new FormatException("TLV truncated");
            ulong v = 0;
            for (int i = 0; i < n; i++) v |= (ulong)_d[_pos++] << (8 * i);
            return v;
        }
    }
}
