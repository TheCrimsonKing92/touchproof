using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: only owned in-memory streams and event gates, no process/global state.
public sealed class PipeDrainReceivedByteCountTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void ReceivedByteCount_PartialUtf8AndPreamble_CountBeforeTextDecodes(int prefix)
    {
        const string text = "€🧪x";
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
        using var source = new GatedStream(bytes, prefix);
        var drain = PipeDrain.Start(source, Encoding.UTF8, "byte-count-partial");
        try
        {
            Assert.True(source.WaitingForTail.Wait(TimeSpan.FromSeconds(120)),
                "The drain did not receive the prefix and request the gated tail.");
            Assert.Equal(prefix, drain.ReceivedByteCount);
            Assert.Equal(string.Empty, drain.Text);
        }
        finally { source.ReleaseTail.Set(); }
        Assert.True(drain.Join(Environment.TickCount64 + 120_000), "The released byte drain did not reach EOF.");
        Assert.Equal(bytes.Length, drain.ReceivedByteCount);
        Assert.Equal(text, drain.Text);
    }

    [Fact]
    public void ReceivedByteCount_InvalidEncoding_CountsRawBytesAndPreservesOutput()
    {
        byte[] bytes = [0xff, 0xfe, 0xff, 0x41, 0x00];
        using var expectedStream = new MemoryStream(bytes);
        using var expectedReader = new StreamReader(expectedStream, Encoding.UTF8, true);
        var expected = expectedReader.ReadToEnd();
        using var source = new MemoryStream(bytes);
        var drain = PipeDrain.Start(source, Encoding.UTF8, "byte-count-invalid");
        Assert.True(drain.Join(Environment.TickCount64 + 120_000), "The byte drain did not reach EOF.");
        Assert.Equal(bytes.Length, drain.ReceivedByteCount);
        Assert.Equal(expected, drain.Text);
        Assert.True(source.CanRead); // Ownership remains with the caller.
    }

    private sealed class GatedStream(byte[] bytes, int prefix) : Stream
    {
        private int position;
        internal ManualResetEventSlim WaitingForTail { get; } = new();
        internal ManualResetEventSlim ReleaseTail { get; } = new();
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (position == bytes.Length) return 0;
            if (position == prefix)
            {
                WaitingForTail.Set();
                if (!ReleaseTail.Wait(TimeSpan.FromSeconds(120)))
                    throw new IOException("The test did not release the byte-stream tail.");
            }
            var read = Math.Min(count, (position == 0 ? prefix : bytes.Length) - position);
            Array.Copy(bytes, position, buffer, offset, read);
            position += read;
            return read;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { WaitingForTail.Dispose(); ReleaseTail.Dispose(); }
            base.Dispose(disposing);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
