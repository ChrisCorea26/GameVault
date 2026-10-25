namespace GameVault.ViewModels.Services;

/// <summary>Facts about how the app was launched.</summary>
/// <param name="IsSampleData">True when started with <c>--sample-data</c> (fake services, no ROMs needed).</param>
/// <param name="StartPage">The page to open first, from <c>--page=&lt;name&gt;</c> (for previews and CI smoke tests).</param>
/// <param name="IsEmptySampleLibrary">
/// <c>--sample-data=empty</c>: sample services with an empty library, to preview the first-run screen.
/// </param>
public sealed record AppEnvironment(bool IsSampleData, AppPage StartPage = AppPage.Library, bool IsEmptySampleLibrary = false)
{
    public const string SampleDataArgument = "--sample-data";
    public const string EmptySampleDataArgument = "--sample-data=empty";
    public const string PageArgumentPrefix = "--page=";

    public static AppEnvironment FromCommandLine(IEnumerable<string> args)
    {
        var sample = false;
        var empty = false;
        var page = AppPage.Library;
        foreach (var arg in args)
        {
            if (string.Equals(arg, SampleDataArgument, StringComparison.OrdinalIgnoreCase))
            {
                sample = true;
            }
            else if (string.Equals(arg, EmptySampleDataArgument, StringComparison.OrdinalIgnoreCase))
            {
                sample = true;
                empty = true;
            }
            else if (arg.StartsWith(PageArgumentPrefix, StringComparison.OrdinalIgnoreCase)
                && Enum.TryParse<AppPage>(arg[PageArgumentPrefix.Length..], ignoreCase: true, out var parsed))
            {
                page = parsed;
            }
        }

        return new AppEnvironment(sample, page, empty);
    }
}
