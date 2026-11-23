// DisplayStateMonitor.InterpretDisplayBroadcast is the pure part of the display-off signal; the
// window, the registration and the message pump need a live Win32 queue, same as LidSwitchMonitor.

using System;
using FluentAssertions;
using OmenCore.Hardware;
using Xunit;

namespace OmenCoreApp.Tests.Hardware
{
    public class DisplayStateMonitorTests
    {
        private static readonly Guid ConsoleDisplayStateGuid = new("6FE69556-704A-47A0-8F24-C28D936FDA47");
        private static readonly Guid LidSwitchGuid = new("BA3E0F4D-B817-4094-A2D1-D56379E6A0F3");

        [Fact]
        public void InterpretDisplayBroadcast_DataZero_MeansOff()
        {
            DisplayStateMonitor.InterpretDisplayBroadcast(ConsoleDisplayStateGuid, dataLength: 4, dataValue: 0)
                .Should().BeTrue();
        }

        [Fact]
        public void InterpretDisplayBroadcast_DataOne_MeansOn()
        {
            DisplayStateMonitor.InterpretDisplayBroadcast(ConsoleDisplayStateGuid, dataLength: 4, dataValue: 1)
                .Should().BeFalse();
        }

        [Fact]
        public void InterpretDisplayBroadcast_Dimmed_CountsAsOn()
        {
            // A dimmed display is still lit and the adapter still drives it.
            DisplayStateMonitor.InterpretDisplayBroadcast(ConsoleDisplayStateGuid, dataLength: 4, dataValue: 2)
                .Should().BeFalse();
        }

        [Fact]
        public void InterpretDisplayBroadcast_OtherSetting_ReturnsNull()
        {
            DisplayStateMonitor.InterpretDisplayBroadcast(LidSwitchGuid, dataLength: 4, dataValue: 0)
                .Should().BeNull();
        }

        [Fact]
        public void InterpretDisplayBroadcast_PayloadTooShort_ReturnsNull()
        {
            DisplayStateMonitor.InterpretDisplayBroadcast(ConsoleDisplayStateGuid, dataLength: 2, dataValue: 0)
                .Should().BeNull();
        }

        [Fact]
        public void IdleFromTicks_Plain()
        {
            DisplayStateMonitor.IdleFromTicks(nowTicks: 100_000, lastInputTicks: 40_000)
                .Should().Be(TimeSpan.FromSeconds(60));
        }

        [Fact]
        public void IdleFromTicks_AcrossTickCountWrap()
        {
            // GetTickCount wraps every 49.7 days; last input just before the wrap, now just after.
            DisplayStateMonitor.IdleFromTicks(nowTicks: 5_000, lastInputTicks: uint.MaxValue - 4_999)
                .Should().Be(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public void GpuIdlePauseThreshold_IsBelowShortestDisplayTimeout()
        {
            // Windows' shortest display timeout, and the lock screen's, is one minute.
            DisplayStateMonitor.GpuIdlePauseThreshold.Should().BeLessThan(TimeSpan.FromMinutes(1));
        }
    }
}
