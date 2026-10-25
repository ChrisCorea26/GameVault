namespace GameVault.ViewModels.Services;

/// <summary>Facts about how the app was launched.</summary>
/// <param name="IsSampleData">True when started with <c>--sample-data</c> (fake services, no ROMs needed).</param>
/// <param name="StartPage">The page to open first, from <c>--page=&lt;name&gt;</c> (for previews and CI smoke tests).</param>
public sealed record AppEnvironment(bool IsSampleData, AppPage StartPage = AppPage.Library)
{
    public const string SampleDataArgument = "--sample-data";
    public const string PageArgumentPrefix = "--page=";

    public static AppEnvironment FromCommandLine(IEnumerable<string> args)
    {
        var sample = false;
        var page = AppPage.Library;
        foreach (var arg in args)
        {
            if (string.Equals(arg, SampleDataArgument, StringComparison.OrdinalIgnoreCase))
            {
                sample = true;
            }
            else if (arg.StartsWith(PageArgumentPrefix, StringComparison.OrdinalIgnoreCase)
                && Enum.TryParse<AppPage>(arg[PageArgumentPrefix.Length..], ignoreCase: true, out var parsed))
            {
                page = parsed;
            }
        }

        return new AppEnvironment(sample, page);
    }
}
