namespace GameVault.ViewModels.Services;

/// <summary>Facts about how the app was launched.</summary>
/// <param name="IsSampleData">True when started with <c>--sample-data</c> (fake services, no ROMs needed).</param>
public sealed record AppEnvironment(bool IsSampleData)
{
    public const string SampleDataArgument = "--sample-data";

    public static AppEnvironment FromCommandLine(IEnumerable<string> args) =>
        new(args.Any(a => string.Equals(a, SampleDataArgument, StringComparison.OrdinalIgnoreCase)));
}
