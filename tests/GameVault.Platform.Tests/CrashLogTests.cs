using GameVault.Platform.Storage;

namespace GameVault.Platform.Tests;

public sealed class CrashLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GameVaultTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Appends_exceptions_with_their_source()
    {
        var paths = new AppPaths(_root);

        CrashLog.Write(paths, new InvalidOperationException("first"), "Test");
        CrashLog.Write(paths, new InvalidOperationException("second"), "Test");

        var text = File.ReadAllText(paths.CrashLogFile);
        text.Should().Contain("first").And.Contain("second").And.Contain("InvalidOperationException");
    }
}
