using Wic64Server.Content;

namespace Wic64Server.Programs;

/// <summary>
/// What was pushed to the C64 (make push or the web UI): a program to run, a picture to show or a
/// tune to play. The browser on the C64 picks it up on its next check.
/// </summary>
public sealed class PushQueue
{
    public enum Kind { Program, Picture, Tune }

    /// <summary>Program: Name (PETSCII) and Program bytes. Picture and tune: the file's Path.</summary>
    public sealed record Pushed(Kind What, byte[] Name, byte[]? Program = null, bool Save = false, string? Path = null)
    {
        public string Title => What == Kind.Program ? Petscii.ToText(Name) : System.IO.Path.GetFileName(Path!);

        /// <summary>The C64's check: 1 = run, 2 = save and run, 3 = show picture, 4 = play tune.</summary>
        public byte State => What switch
        {
            Kind.Program => (byte)(Save ? 2 : 1),
            Kind.Picture => 3,
            _ => 4,
        };
    }

    Pushed? pending;

    public void Push(Pushed item) => Interlocked.Exchange(ref pending, item);

    public Pushed? Peek() => Volatile.Read(ref pending);

    public Pushed? Take() => Interlocked.Exchange(ref pending, null);

    /// <summary>Takes the pending item only if it is of the given kind.</summary>
    public Pushed? Take(Kind kind)
    {
        var current = Volatile.Read(ref pending);
        return current?.What == kind && Interlocked.CompareExchange(ref pending, null, current) == current ? current : null;
    }
}
