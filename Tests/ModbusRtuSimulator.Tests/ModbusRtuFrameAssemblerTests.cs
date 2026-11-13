using ModbusRtuSimulator.Protocol;
using ModbusRtuSimulator.Services;
using Xunit;

namespace ModbusRtuSimulator.Tests;

public sealed class ModbusRtuFrameAssemblerTests
{
    [Fact]
    public void Append_CollectsEightByteReadRequestAcrossChunks()
    {
        var assembler = new ModbusRtuFrameAssembler();
        var request = ModbusRtuProcessor.AddCrc(new byte[] { 16, 2, 0, 128, 0, 6 });

        Assert.Empty(assembler.Append(request.AsSpan(0, 5)));
        Assert.Equal(request, Assert.Single(assembler.Append(request.AsSpan(5))));
        Assert.Equal(0, assembler.PendingByteCount);
    }

    [Fact]
    public void Append_EmitsSeveralCompleteFramesFromOneChunk()
    {
        var assembler = new ModbusRtuFrameAssembler();
        var first = ModbusRtuProcessor.AddCrc(new byte[] { 3, 2, 0, 0, 0, 6 });
        var second = ModbusRtuProcessor.AddCrc(new byte[] { 3, 4, 0, 0, 0, 6 });

        var frames = assembler.Append(first.Concat(second).ToArray());

        Assert.Equal(new[] { first, second }, frames);
    }

    [Fact]
    public void Append_UsesByteCountForWriteMultipleRegisterRequest()
    {
        var assembler = new ModbusRtuFrameAssembler();
        var request = ModbusRtuProcessor.AddCrc(
            new byte[] { 1, 16, 0, 0, 0, 2, 4, 0x12, 0x34, 0xAB, 0xCD });

        Assert.Empty(assembler.Append(request.AsSpan(0, 8)));
        Assert.Equal(request, Assert.Single(assembler.Append(request.AsSpan(8))));
    }

    [Fact]
    public void Append_ResynchronizesAfterIncompleteBytesBeforeNextValidRequest()
    {
        var assembler = new ModbusRtuFrameAssembler();
        var incomplete = new byte[] { 16, 2, 0, 128, 0 };
        var request = ModbusRtuProcessor.AddCrc(new byte[] { 3, 4, 0, 0, 0, 6 });

        var frames = assembler.Append(incomplete.Concat(request).ToArray());

        Assert.Equal(2, frames.Count);
        Assert.Equal(incomplete, frames[0]);
        Assert.Equal(request, frames[1]);
    }
}
