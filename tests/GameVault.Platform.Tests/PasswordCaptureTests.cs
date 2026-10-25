using GameVault.Library.Passwords;
using GameVault.Platform.Emulation;
using GameVault.Platform.Input;
using GameVault.Platform.Storage;
using GameVault.Platform.Video;

namespace GameVault.Platform.Tests;

public sealed class PasswordCaptureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GameVaultPasswords", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>NROM program: writes 0..7 to $0500-$0507, then sets $10 = 7 (the "password screen"), then loops.</summary>
    private static byte[] PasswordScreenRom()
    {
        byte[] program =
        [
            0xA2, 0x00,             // LDX #0
            0x8A,                   // loop: TXA
            0x9D, 0x00, 0x05,       // STA $0500,X
            0xE8,                   // INX
            0xE0, 0x08,             // CPX #8
            0xD0, 0xF7,             // BNE loop
            0xA9, 0x07, 0x85, 0x10, // LDA #7; STA $10
            0x4C, 0x0F, 0xC0,       // JMP * ($C00F)
        ];
        var image = new byte[16 + 0x4000 + 0x2000];
        "NES\u001A"u8.CopyTo(image);
        image[4] = 1;
        image[5] = 1;
        program.CopyTo(image, 16);
        image[16 + 0x3FFC] = 0x00;
        image[16 + 0x3FFD] = 0xC0;
        return image;
    }

    [Fact]
    public async Task Password_screens_are_captured_once_with_a_screenshot()
    {
        var definition = PasswordDefinition.Parse("""
            {
              "detect": [ { "address": "0x0010", "value": 7 } ],
              "stableFrames": 5,
              "source": { "from": "ram", "address": "0x0500", "length": 8 },
              "alphabet": "QUESTGAM",
              "groups": [5, 3]
            }
            """);
        using var host = new EmulatorHost(new FrameSink(), new InputService(KeyboardMapping.CreateDefault()))
        {
            PasswordDefinitionResolver = _ => definition,
        };
        var store = new PasswordStore(_root);
        var service = new PasswordService(host, store);
        var captured = new List<string>();
        service.Captured += (_, p) => captured.Add(p);

        host.Load(PasswordScreenRom(), "Password test");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (captured.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        await Task.Delay(300); // the screen stays up: no second capture
        captured.Should().Equal("QUEST GAM");
        var saved = (await service.GetAsync(host.GameKey!)).Should().ContainSingle().Subject;
        saved.Automatic.Should().BeTrue();
        File.Exists(saved.ScreenshotPath).Should().BeTrue();
    }
}
