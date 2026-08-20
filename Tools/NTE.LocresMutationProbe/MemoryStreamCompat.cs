namespace NTE.LocresMutationProbe;

// Keep the probe's size arithmetic in long while adapting to MemoryStream's int capacity API.
internal sealed class MemoryStream : System.IO.MemoryStream
{
    public MemoryStream(byte[] buffer, bool writable) : base(buffer, writable) { }
    public MemoryStream(long capacity) : base(checked((int)capacity)) { }
}
