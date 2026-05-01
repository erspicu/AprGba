using AprCpu.Core;
using Xunit;

namespace AprCpu.Tests;

public class JitSpikeTests
{
    [Fact]
    public void EmitAddIR_ContainsExpectedInstructions()
    {
        var ir = JitSpike.EmitAddIR();

        Assert.Contains("define i32 @add", ir);
        Assert.Contains("add i32", ir);
        Assert.Contains("ret i32", ir);
    }

    [Fact]
    public void DumpAddIRToFile_WritesValidLlFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aprcpu_spike_{Guid.NewGuid():N}.ll");
        try
        {
            JitSpike.DumpAddIRToFile(path);
            Assert.True(File.Exists(path));

            var content = File.ReadAllText(path);
            Assert.Contains("define i32 @add", content);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData(3, 4, 7)]
    [InlineData(0, 0, 0)]
    [InlineData(-1, 1, 0)]
    [InlineData(int.MaxValue, 1, int.MinValue)] // overflow wraps
    public void JitAndRunAdd_ReturnsExpectedSum(int a, int b, int expected)
    {
        var result = JitSpike.JitAndRunAdd(a, b);
        Assert.Equal(expected, result);
    }
}
