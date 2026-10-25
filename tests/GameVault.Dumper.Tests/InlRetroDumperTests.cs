using GameVault.Library.Hashing;

namespace GameVault.Dumper.Tests;

public sealed class InlRetroDumperTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GameVaultDumper", Guid.NewGuid().ToString("N"));
    private readonly string _exe;

    public InlRetroDumperTests()
    {
        Directory.CreateDirectory(_root);
        _exe = Path.Combine(_root, "inlretro.exe");
        File.WriteAllText(_exe, "fake");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static byte[] Image(byte fill)
    {
        var data = new byte[16 + 0x4000];
        "NES\u001A"u8.CopyTo(data);
        data[4] = 1;
        Array.Fill(data, fill, 16, 0x4000);
        return data;
    }

    /// <summary>Writes the next image from the script to the -d path on each run.</summary>
    private sealed class ScriptedRunner(params Func<string, ProcessResult>[] runs) : IProcessRunner
    {
        private int _run;

        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
        {
            Calls.Add(arguments);
            var output = arguments[arguments.ToList().IndexOf("-d") + 1];
            return Task.FromResult(runs[Math.Min(_run++, runs.Length - 1)](output));
        }
    }

    private static Func<string, ProcessResult> Writes(byte fill) => path =>
    {
        File.WriteAllBytes(path, Image(fill));
        return new ProcessResult(0, "Dump complete");
    };

    [Fact]
    public void Command_line_names_the_board_and_sizes()
    {
        InlRetroDumper.Arguments(new DumpRequest(1, 256, 128, "out.nes"), "x.nes").Should().Equal(
            "-s", Path.Combine("scripts", "inlretro2.lua"), "-c", "NES", "-m", "mmc1", "-x", "256", "-y", "128", "-d", "x.nes");
    }

    [Fact]
    public async Task One_read_matching_the_catalog_is_verified()
    {
        var runner = new ScriptedRunner(Writes(7));
        var dumper = new InlRetroDumper(runner, _exe);
        var output = Path.Combine(_root, "game.nes");

        var result = await dumper.DumpAsync(new DumpRequest(0, 16, 0, output, RomHasher.Hash(Image(7)).Sha1));

        result.Status.Should().Be(DumpStatus.Verified);
        result.Attempts.Should().Be(1);
        File.ReadAllBytes(output).Should().Equal(Image(7));
    }

    [Fact]
    public async Task Without_a_catalog_entry_two_matching_reads_are_needed()
    {
        var runner = new ScriptedRunner(Writes(1), Writes(2), Writes(2));
        var result = await new InlRetroDumper(runner, _exe).DumpAsync(new DumpRequest(0, 16, 0, Path.Combine(_root, "g.nes")));

        result.Status.Should().Be(DumpStatus.Consistent);
        result.Attempts.Should().Be(3);
        result.Hashes.Should().Be(RomHasher.Hash(Image(2)));
    }

    [Fact]
    public async Task Reads_that_never_agree_ask_to_clean_the_contacts()
    {
        var runner = new ScriptedRunner(Writes(1), Writes(2), Writes(3));

        var result = await new InlRetroDumper(runner, _exe).DumpAsync(new DumpRequest(0, 16, 0, Path.Combine(_root, "g.nes"), "ffff"));

        result.Status.Should().Be(DumpStatus.BadRead);
        result.Message.Should().Contain("Clean the cartridge contacts");
        runner.Calls.Should().HaveCount(InlRetroDumper.MaxAttempts);
        File.Exists(Path.Combine(_root, "g.nes")).Should().BeFalse();
    }

    [Fact]
    public async Task Device_problems_stop_with_a_plain_message()
    {
        var runner = new ScriptedRunner(_ => new ProcessResult(1, "ERROR: Could not find INL retro-prog USB device"));

        var result = await new InlRetroDumper(runner, _exe).DumpAsync(new DumpRequest(4, 256, 256, Path.Combine(_root, "g.nes")));

        result.Status.Should().Be(DumpStatus.Failed);
        result.Message.Should().Contain("plugged in");
        runner.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task Missing_software_and_unsupported_boards_fail_before_running()
    {
        var missing = await new InlRetroDumper(new ScriptedRunner(Writes(1)), Path.Combine(_root, "nope.exe"))
            .DumpAsync(new DumpRequest(0, 16, 0, "x.nes"));
        missing.Message.Should().Contain("can't find the INL Retro-Prog software");

        var unsupported = await new InlRetroDumper(new ScriptedRunner(Writes(1)), _exe).DumpAsync(new DumpRequest(5, 256, 256, "x.nes"));
        unsupported.Message.Should().Contain("can't read this kind of cartridge");
    }
}
