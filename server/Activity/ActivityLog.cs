namespace Wic64Server.Activity;

/// <summary>Recent C64 activity for the web UI, and when the C64 was last seen.</summary>
public sealed class ActivityLog
{
    public sealed record Event(long Id, DateTime Time, string Kind, string Text);

    const int MaxEvents = 200;

    readonly LinkedList<Event> events = new();
    readonly Lock eventLock = new();
    long nextId;

    public DateTime? LastSeen { get; private set; }
    public string? LastAddress { get; private set; }

    public void Seen(string? address)
    {
        LastSeen = DateTime.UtcNow;
        LastAddress = address;
    }

    public void Add(string kind, string text)
    {
        lock (eventLock)
        {
            events.AddLast(new Event(++nextId, DateTime.UtcNow, kind, text));
            if (events.Count > MaxEvents)
                events.RemoveFirst();
        }
    }

    /// <summary>Events after the given id, oldest first.</summary>
    public IReadOnlyList<Event> Since(long id)
    {
        lock (eventLock)
            return events.Where(e => e.Id > id).ToList();
    }
}
