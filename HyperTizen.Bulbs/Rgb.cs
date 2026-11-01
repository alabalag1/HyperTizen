namespace HyperTizen.Bulbs
{
    /// <summary>10-bit RGB sample (0-1023) as read from the TV.</summary>
    public readonly struct Rgb10
    {
        public readonly int R, G, B;
        public Rgb10(int r, int g, int b) { R = r; G = g; B = b; }
    }

    /// <summary>RGB with channels 0-255.</summary>
    public readonly struct Rgb
    {
        public readonly double R, G, B;
        public Rgb(double r, double g, double b) { R = r; G = g; B = b; }
    }
}
