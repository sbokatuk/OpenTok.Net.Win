using System.IO.Compression;
using Xunit;

namespace OpenTok.Net.Win.PackageTests;

/// <summary>
/// Asserts what <c>OpenTok.Net.Win</c> ships, read from the packed .nupkg rather than from the
/// project file — so a pack step that quietly dropped a target framework or the architecture guard
/// fails here rather than at a consumer.
/// </summary>
public class PackageTests
{
    private const string PackageId = "OpenTok.Net.Win";

    /// <summary>
    /// Every target framework the package must carry. Pinned rather than discovered: a package that
    /// lost one because a pack pass failed is exactly the regression this exists to catch.
    /// </summary>
    public static readonly string[] TargetFrameworks =
    [
        "net8.0-windows10.0.19041", "net9.0-windows10.0.19041", "net10.0-windows10.0.19041",
    ];

    public static IEnumerable<object[]> Frameworks => TargetFrameworks.Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(Frameworks))]
    public void Carries_an_assembly_for_each_target_framework(string tfm)
    {
        using var package = OpenPackage();

        var expected = $"lib/{tfm}/{PackageId}.dll";
        Assert.True(package.GetEntry(expected) is not null, $"missing '{expected}'.");
    }

    [Fact]
    public void Ships_the_architecture_guard()
    {
        // build/OpenTok.Net.Win.targets is what turns the x64-only native payload into a build
        // error instead of a BadImageFormatException after launch. It only works if it is actually
        // in the package under build/, where NuGet imports it automatically — a PackagePath typo
        // leaves the package installable, buildable and silently unguarded.
        using var package = OpenPackage();

        Assert.True(
            package.GetEntry($"build/{PackageId}.targets") is not null,
            "the package does not carry build/OpenTok.Net.Win.targets, so the arm64 guard never runs.");
    }

    [Fact]
    public void Depends_on_the_vonage_windows_sdk()
    {
        // The whole point of the package is to render frames from OpenTok.Client. A consumer adding
        // this one should not also have to know to add that one.
        using var package = OpenPackage();

        Assert.Contains("id=\"OpenTok.Client\"", ReadNuspec(package), StringComparison.Ordinal);
    }

    [Fact]
    public void Depends_on_the_windows_app_sdk()
    {
        // WinUI 3 itself. Without it the renderer's WriteableBitmap and DispatcherQueue do not
        // resolve, and the failure lands in the consumer's build rather than here.
        using var package = OpenPackage();

        Assert.Contains("id=\"Microsoft.WindowsAppSDK\"", ReadNuspec(package), StringComparison.Ordinal);
    }

    [Fact]
    public void Says_in_its_description_that_it_is_x64_only()
    {
        // The one limitation a consumer cannot discover until runtime, and cannot work around. It
        // belongs on the nuget.org listing, not only in the README.
        using var package = OpenPackage();

        Assert.Contains("x64", ReadNuspec(package), StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadNuspec(ZipArchive package)
    {
        var entry = package.Entries.Single(e => e.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static ZipArchive OpenPackage()
    {
        var directory = ArtifactsDirectory();

        var matches = Directory.Exists(directory)
            ? Directory.GetFiles(directory, $"{PackageId}.*.nupkg")
                .Where(f => !f.EndsWith(".symbols.nupkg", StringComparison.Ordinal))
                .Where(f =>
                {
                    var remainder = Path.GetFileName(f)[(PackageId.Length + 1)..];
                    return remainder.Length > 0 && char.IsDigit(remainder[0]);
                })
                .ToArray()
            : [];

        Assert.True(
            matches.Length > 0,
            $"No {PackageId}.<version>.nupkg found in '{directory}'. Pack src/OpenTok.Net.Win first.");

        return ZipFile.OpenRead(matches.OrderByDescending(File.GetLastWriteTimeUtc).First());
    }

    private static string ArtifactsDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ?? AppContext.BaseDirectory;

        return Environment.GetEnvironmentVariable("OPENTOK_ARTIFACTS") is { Length: > 0 } configured
            ? Path.GetFullPath(configured, root)
            : Path.Combine(root, "artifacts");
    }
}
