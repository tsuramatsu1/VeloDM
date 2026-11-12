using System.Text.Json.Serialization;

namespace VeloDM.Core;

/// <summary>
/// A byte range of the target file served by one connection. <see cref="End"/> is
/// inclusive and is -1 when the total size is unknown (read until EOF). Another
/// connection may shrink <see cref="End"/> while this one is running (dynamic
/// segmentation), so End and Downloaded are only changed under <see cref="Sync"/>.
/// </summary>
public sealed class Segment
{
    public long Start { get; set; }
    public long End { get; set; } = -1;
    public long Downloaded { get; set; }
    public bool Eof { get; set; }

    [JsonIgnore] public object Sync { get; } = new();
    [JsonIgnore] public bool Active { get; set; }
    [JsonIgnore] public string State { get; set; } = "";
    [JsonIgnore] public long LastRunBytes { get; set; }

    [JsonIgnore] public long Position => Start + Downloaded;
    [JsonIgnore] public long Remaining => End < 0 ? long.MaxValue : End - Position + 1;
    [JsonIgnore] public bool IsComplete => End >= 0 ? Position > End : Eof;
}

public sealed record SegmentSnapshot(int Index, long Start, long End, long Downloaded, bool Active, bool Complete, string State);
