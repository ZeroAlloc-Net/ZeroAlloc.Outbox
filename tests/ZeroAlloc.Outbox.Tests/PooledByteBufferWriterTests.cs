namespace ZeroAlloc.Outbox.Tests;

public sealed class PooledByteBufferWriterTests
{
    [Fact]
    public void Growing_Keeps_The_Bytes_Already_Written()
    {
        var writer = new PooledByteBufferWriter();
        writer.GetSpan(3)[..3].Fill(7);
        writer.Advance(3);

        var span = writer.GetSpan(10_000);
        span.Length.Should().BeGreaterThanOrEqualTo(10_000);
        span[0] = 8;
        writer.Advance(1);

        writer.WrittenSpan.ToArray().Should().Equal(7, 7, 7, 8);
    }

    [Fact]
    public void Reset_Forgets_The_Written_Bytes()
    {
        var writer = new PooledByteBufferWriter();
        writer.GetSpan(4);
        writer.Advance(4);

        writer.Reset();

        writer.WrittenSpan.Length.Should().Be(0);
        writer.GetMemory().Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Advancing_Past_The_Buffer_Throws()
    {
        var writer = new PooledByteBufferWriter();
        var available = writer.GetSpan(1).Length;

        var act = () => writer.Advance(available + 1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void A_Negative_Size_Hint_Throws()
    {
        var writer = new PooledByteBufferWriter();

        Action act = () => writer.GetSpan(-1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
