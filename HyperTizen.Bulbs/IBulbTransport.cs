using System.Threading;
using System.Threading.Tasks;

namespace HyperTizen.Bulbs
{
    /// <summary>Sends light commands to a bulb. <c>tenths</c> is the transition time in 1/10 s. A result of
    /// <c>false</c> means the command was not confirmed by the bulb.</summary>
    public interface IBulbTransport
    {
        Task<bool> MoveToHueAndSaturation(ulong node, int hue, int sat, int tenths, CancellationToken ct);
        Task<bool> MoveToLevelWithOnOff(ulong node, int level, int tenths, CancellationToken ct);
        Task<bool> MoveToColorTemperature(ulong node, int mireds, int tenths, CancellationToken ct);
    }
}
