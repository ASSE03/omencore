using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using OmenCore.Hardware;
using OmenCore.Services;
using Xunit;

namespace OmenCoreApp.Tests.Hardware
{
    /// <summary>
    /// The --nvapi-host loop answers one JSON line per request with the result and the full
    /// property state, reports unknown operations as failures without stopping, and ends when its
    /// input closes. Initialize is not sent here: it would load NVAPI on a machine that has it.
    /// </summary>
    public class NvapiHostProtocolTests
    {
        private static string[] Run(params string[] requests)
        {
            var input = new StringReader(string.Join("\n", requests) + "\n");
            var output = new StringWriter();
            var code = NvapiService.RunHost(new LoggingService(), input, output);
            code.Should().Be(0);
            return output.ToString().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        }

        [Fact]
        public void AnswersEachRequestOnItsOwnLine_WithState()
        {
            var lines = Run("{\"op\":\"GetGpuLoad\"}", "{\"op\":\"GetCurrentClocks\"}");

            lines.Should().HaveCount(2);
            using var first = JsonDocument.Parse(lines[0]);
            first.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
            first.RootElement.GetProperty("result").GetInt32().Should().Be(-1, "nothing was initialised");
            first.RootElement.GetProperty("state").GetProperty("Initialized").GetBoolean().Should().BeFalse();
            first.RootElement.GetProperty("state").GetProperty("PowerLimitPercent").GetInt32().Should().Be(100);
        }

        [Fact]
        public void UnknownOperation_IsReportedAndTheLoopContinues()
        {
            var lines = Run("{\"op\":\"Nope\"}", "{\"op\":\"GetGpuLoad\"}");

            lines.Should().HaveCount(2);
            JsonDocument.Parse(lines[0]).RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
            JsonDocument.Parse(lines[1]).RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        }

        [Fact]
        public void HostExecutablePath_IsNullByDefault_SoTestsRunInProcess()
        {
            NvapiService.HostExecutablePath.Should().BeNull();
        }
    }
}
