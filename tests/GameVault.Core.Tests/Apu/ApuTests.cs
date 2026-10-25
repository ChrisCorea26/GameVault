using GameVault.Core.Apu;
using GameVault.Core.Audio;
using GameVault.Core.Tests.State;

namespace GameVault.Core.Tests.Apu;

/// <summary>Unit tests for the 2A03 APU (NESdev Wiki: "APU", "APU Frame Counter").</summary>
public class ApuTests
{
    private readonly Apu2A03 _apu = new();
    private long _cycle;

    public ApuTests()
    {
        _apu.CpuCycle = () => _cycle;
        _apu.Power();
        Run(10); // let the power-on $4017 write take effect
    }

    private void Run(int cycles)
    {
        for (var i = 0; i < cycles; i++)
        {
            _cycle++;
            _apu.Tick();
        }
    }

    private byte Status() => _apu.ReadStatus(0);

    [Fact]
    public void Length_counter_loads_from_the_table_and_reports_in_status()
    {
        _apu.WriteRegister(0x4015, 0x01);
        _apu.WriteRegister(0x4003, 0x08); // index 1 -> 254
        Run(1);

        (Status() & 0x01).Should().Be(0x01);
    }

    [Fact]
    public void Disabled_channels_ignore_length_loads()
    {
        _apu.WriteRegister(0x4015, 0x00);
        _apu.WriteRegister(0x4003, 0x08);
        Run(1);

        (Status() & 0x01).Should().Be(0);
    }

    [Fact]
    public void Length_counter_runs_out_on_half_frame_clocks()
    {
        _apu.WriteRegister(0x4017, 0x40); // 4-step, no IRQ
        Run(10);
        _apu.WriteRegister(0x4015, 0x01);
        _apu.WriteRegister(0x4003, 0x18); // index 3 -> length 2
        Run(1);

        Run(29830); // one 4-step sequence = two half-frame clocks

        (Status() & 0x01).Should().Be(0);
    }

    [Fact]
    public void Four_step_mode_sets_the_frame_irq_flag_and_reading_status_clears_it()
    {
        _apu.WriteRegister(0x4017, 0x00);
        Run(30000);

        _apu.FrameIrq.Should().BeTrue();
        (Status() & 0x40).Should().Be(0x40);
        _apu.FrameIrq.Should().BeFalse();
    }

    [Fact]
    public void Irq_inhibit_and_five_step_mode_suppress_the_frame_irq()
    {
        _apu.WriteRegister(0x4017, 0x40);
        Run(40000);
        _apu.FrameIrq.Should().BeFalse();

        _apu.WriteRegister(0x4017, 0x80);
        Run(40000);
        _apu.FrameIrq.Should().BeFalse();
    }

    [Fact]
    public void Writing_4017_with_inhibit_clears_a_pending_irq()
    {
        _apu.WriteRegister(0x4017, 0x00);
        Run(30000);

        _apu.WriteRegister(0x4017, 0x40);

        _apu.FrameIrq.Should().BeFalse();
    }

    [Fact]
    public void Dmc_reports_active_requests_dma_and_raises_irq_at_the_end()
    {
        var fetches = 0;
        _apu.WriteRegister(0x4010, 0x8F); // IRQ on, fastest rate
        _apu.WriteRegister(0x4013, 0x00); // 1 byte
        _apu.WriteRegister(0x4015, 0x10);
        (Status() & 0x10).Should().Be(0x10);

        for (var i = 0; i < 5000; i++)
        {
            Run(1);
            if (_apu.TakeDmcDmaRequest())
            {
                fetches++;
                ((GameVault.Core.Cpu.IDmcDmaSource)_apu).OnDmcDmaRead(0x55);
            }
        }

        fetches.Should().Be(1);
        (Status() & 0x10).Should().Be(0);
        _apu.DmcIrq.Should().BeTrue();

        _apu.WriteRegister(0x4015, 0x00);
        _apu.DmcIrq.Should().BeFalse("writing $4015 acknowledges the DMC IRQ");
    }

    [Fact]
    public void Status_bit_5_is_open_bus()
    {
        (_apu.ReadStatus(0xFF) & 0x20).Should().Be(0x20);
        (_apu.ReadStatus(0x00) & 0x20).Should().Be(0);
    }

    [Fact]
    public void Mixer_tables_match_the_nesdev_formulas()
    {
        var sink = new RecordingSink();
        _apu.AudioSink = sink;
        Run(1);
        var before = sink.Level;

        // At power the triangle holds step 7, so the TND input moves from 3*7 to 3*7 + 127.
        _apu.WriteRegister(0x4011, 0x7F);
        Run(1);

        static float Tnd(int n) => 163.67f / (24329f / n + 100);
        (sink.Level - before).Should().BeApproximately(Tnd(21 + 127) - Tnd(21), 1e-5f);
    }

    [Fact]
    public void Audio_frames_report_their_length_in_cycles()
    {
        var sink = new RecordingSink();
        _apu.AudioSink = sink;
        _apu.EndAudioFrame();
        Run(1234);

        _apu.EndAudioFrame().Should().Be(1234);
        sink.LastFrameClocks.Should().Be(1234);
    }

    [Fact]
    public void State_round_trips_with_every_channel_active()
    {
        StateAssert.RoundTrips(() =>
        {
            var apu = new Apu2A03();
            apu.Power();
            return apu;
        }, apu =>
        {
            apu.WriteRegister(0x4015, 0x1F);
            apu.WriteRegister(0x4000, 0xBF);
            apu.WriteRegister(0x4001, 0x8A);
            apu.WriteRegister(0x4003, 0x22);
            apu.WriteRegister(0x4008, 0x81);
            apu.WriteRegister(0x400B, 0x30);
            apu.WriteRegister(0x400E, 0x84);
            apu.WriteRegister(0x400F, 0x18);
            apu.WriteRegister(0x4010, 0x4F);
            apu.WriteRegister(0x4017, 0x80);
            for (var i = 0; i < 12345; i++)
            {
                apu.Tick();
                if (apu.TakeDmcDmaRequest())
                {
                    ((GameVault.Core.Cpu.IDmcDmaSource)apu).OnDmcDmaRead((byte)i);
                }
            }
        });
    }

    private sealed class RecordingSink : IAudioSink
    {
        public float Level { get; private set; }

        public int LastFrameClocks { get; private set; }

        public void AddDelta(int clock, float delta) => Level += delta;

        public void EndFrame(int clocks) => LastFrameClocks = clocks;
    }
}
