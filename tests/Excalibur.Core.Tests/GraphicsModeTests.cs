using Excalibur.Core.Graphics;
using Excalibur.Core.Hardware;

namespace Excalibur.Core.Tests;

public class GraphicsModeTests
{
    private sealed class Firmware(SmiCommand reply) : ISmiChannel
    {
        public SmiCommand? Sent;
        public SmiCommand Execute(SmiCommand request) { Sent = request; return reply; }
    }

    [Theory]
    [InlineData(1u, 1u, GraphicsMode.Hybrid, true)]
    [InlineData(2u, 0u, GraphicsMode.Discrete, false)]
    [InlineData(3u, 0u, GraphicsMode.Integrated, false)]
    public void The_reply_gives_the_mode_and_the_HDMI_cable(uint a2, uint a3, GraphicsMode mode, bool hdmi)
    {
        var fw = new Firmware(new SmiCommand { A2 = a2, A3 = a3, A4 = a2 });
        Assert.Equal(new GraphicsReading(mode, hdmi), new GraphicsModeReader(fw).Read());
        Assert.Equal(SmiCommand.OpGet, fw.Sent!.Value.A0);
        Assert.Equal((ushort)0x0203, fw.Sent.Value.A1);
    }

    [Theory]
    [InlineData(0u)] // a G770 echoes the request
    [InlineData(4u)]
    public void A_reply_without_a_mode_is_not_supported(uint a2)
        => Assert.Null(GraphicsModeReader.Parse(new SmiCommand { A2 = a2 }));

    [Fact]
    public void The_monitor_logs_the_first_answer_and_mode_changes_only()
    {
        var log = new List<string>();
        GraphicsReading? next = new(GraphicsMode.Hybrid, false);
        var monitor = new GraphicsModeMonitor(() => next, log.Add);
        monitor.Refresh("start");
        next = new GraphicsReading(GraphicsMode.Hybrid, true);
        monitor.Refresh("HDMI");
        Assert.Equal(next, monitor.Current); // the cable is kept, not logged
        next = new GraphicsReading(GraphicsMode.Integrated, false);
        monitor.Refresh("wake");
        Assert.Equal(["graphics: Hybrid (start)", "graphics: Integrated (wake)"], log);
    }

    [Fact]
    public void A_failed_read_keeps_the_last_reading()
    {
        var log = new List<string>();
        bool fail = false;
        var monitor = new GraphicsModeMonitor(() => fail ? throw new InvalidOperationException("RW_GMWMI is not present") : new GraphicsReading(GraphicsMode.Integrated, false), log.Add);
        monitor.Refresh("start");
        fail = true;
        monitor.Refresh("wake");
        Assert.Equal(GraphicsMode.Integrated, monitor.Current!.Mode);
        Assert.Contains(log, l => l.StartsWith("graphics: mode not read (wake)"));
    }

    [Fact]
    public void A_BIOS_without_the_command_is_logged_once()
    {
        var log = new List<string>();
        var monitor = new GraphicsModeMonitor(() => null, log.Add);
        monitor.Refresh("start");
        monitor.Refresh("wake");
        Assert.Null(monitor.Current);
        Assert.Single(log);
    }
}
