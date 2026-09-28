using System.Text;

namespace Wic64Server.Programs;

/// <summary>
/// Plugins are C64 programs in build/plugins (assembled from c64/plugins) that the browser starts like any
/// program. They carry the marker below, followed by a length byte and 30 bytes: the server fills in the
/// address the C64 reached it on, so the plugin can talk to the server and load the browser again.
/// </summary>
public static class Plugins
{
    static readonly byte[] Marker = "WIC64-SERVER-ADDRESS"u8.ToArray();
    const int MaxAddress = 30;

    public static byte[] WithServerAddress(byte[] program, string host)
    {
        var at = program.AsSpan().IndexOf(Marker);
        if (at < 0)
            return program;

        var address = Encoding.ASCII.GetBytes(host);
        if (address.Length > MaxAddress)
            throw new InvalidOperationException($"server address too long for a plugin: {host}");

        var patched = (byte[])program.Clone();
        patched[at + Marker.Length] = (byte)address.Length;
        address.CopyTo(patched, at + Marker.Length + 1);
        return patched;
    }
}
