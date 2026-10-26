namespace GameVault.ViewModels.Services;

/// <summary>Facts about how the app was launched.</summary>
/// <param name="IsSampleData">True when started with <c>--sample-data</c> (fake services, no ROMs needed).</param>
/// <param name="StartPage">The page to open first, from <c>--page=&lt;name&gt;</c> (for previews and CI smoke tests).</param>
/// <param name="IsEmptySampleLibrary">
/// <c>--sample-data=empty</c>: sample services with an empty library, to preview the first-run screen.
/// </param>
/// <param name="ProfileId">The family profile to run as, from <c>--profile=&lt;id&gt;</c> (SPEC §7.9); null for the main one.</param>
public sealed record AppEnvironment(bool IsSampleData, AppPage StartPage = AppPage.Library, bool IsEmptySampleLibrary = false, string? ProfileId = null)
{
    public const string SampleDataArgument = "--sample-data";
    public const string EmptySampleDataArgument = "--sample-data=empty";
    public const string PageArgumentPrefix = "--page=";
    public const string ProfileArgumentPrefix = "--profile=";

    public static AppEnvironment FromCommandLine(IEnumerable<string> args)
    {
        var sample = false;
        var empty = false;
        var page = AppPage.Library;
        string? profile = null;
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
            else if (arg.StartsWith(ProfileArgumentPrefix, StringComparison.OrdinalIgnoreCase)
                && arg[ProfileArgumentPrefix.Length..] is { Length: > 0 } id
                && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            {
                profile = id;
            }
        }

        return new AppEnvironment(sample, page, empty, profile);
    }
}
