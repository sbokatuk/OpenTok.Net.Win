using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
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

    /// <summary>
    /// <c>OpenTok.Client</c>'s native payload — the x64-only DLLs its own build/ targets copy into an
    /// application's output. This package must reference them and never reproduce them.
    /// </summary>
    private static readonly string[] NativePayload =
    [
        "opentok.dll", "DshowCapturer.dll", "MFCapturer.dll", "OpenTokMMDevice.dll",
    ];

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
    public void Does_not_carry_the_vonage_sdk_native_payload()
    {
        // OpenTok.Client's own targets glob its package directory and add opentok.dll and the three
        // capturers as Content with CopyToOutputDirectory. Right for an application, wrong for a
        // library being packed — see Flows_the_vonage_sdk_build_assets_to_consumers for what those
        // Content items did to a consumer, and src/OpenTok.Net.Win/OpenTok.Net.Win.csproj for why
        // the build assets are excluded rather than the files filtered out at pack time.
        //
        // Asserted rather than trusted, because the failure is entirely downstream: the package
        // packs, uploads and installs cleanly, and only a consumer's build ever says otherwise.
        using var package = OpenPackage();

        var native = package.Entries
            .Where(e => e.FullName.StartsWith("lib/", StringComparison.Ordinal))
            .Select(e => Path.GetFileName(e.FullName))
            .Where(n => NativePayload.Contains(n, StringComparer.OrdinalIgnoreCase))
            .Distinct()
            .ToList();

        Assert.True(
            native.Count == 0,
            $"the package carries OpenTok.Client's native payload ({string.Join(", ", native)}). " +
            "It reaches a consumer through the OpenTok.Client dependency; a second copy here records " +
            "paths that do not exist and breaks the consumer's build with MSB3030.");
    }

    [Theory]
    [MemberData(nameof(Frameworks))]
    public void Does_not_name_the_native_payload_in_its_resource_index(string tfm)
    {
        // The regression that produced four MSB3030s in the sample, guarded where it actually lived.
        // Absence from the file list above was never enough: MakePri indexes this library's
        // @(Content) into OpenTok.Net.Win.pri, and a consumer expands that .pri and copies every
        // payload file it names, resolved next to the .pri itself. Naming a file the package does
        // not carry is therefore worse than carrying it.
        //
        // Read as bytes rather than parsed. The point is only whether these four names appear at
        // all, and a .pri is a string pool — UTF-16 in practice, checked as UTF-8 and without
        // regard to case rather than relying on that. Vacuous against a package from
        // build/PackCheck.sh, whose .pri files are empty placeholders; CI runs this against the real
        // Windows-packed .nupkg, which is where it has teeth.
        using var package = OpenPackage();

        var entry = package.GetEntry($"lib/{tfm}/{PackageId}.pri");
        Assert.True(entry is not null, $"missing 'lib/{tfm}/{PackageId}.pri'.");

        using var contents = new MemoryStream();
        using (var stream = entry!.Open())
        {
            stream.CopyTo(contents);
        }

        var index = contents.ToArray();

        var named = NativePayload.Where(n => Names(index, n)).ToList();

        Assert.True(
            named.Count == 0,
            $"lib/{tfm}/{PackageId}.pri names OpenTok.Client's native payload ({string.Join(", ", named)}). " +
            "A consumer expands this index and copies what it names from beside it, so every name here " +
            "that the package does not carry is an MSB3030 in that consumer's build.");
    }

    [Fact]
    public void Flows_the_vonage_sdk_build_assets_to_consumers()
    {
        // The half of the packaging that is invisible from the file list, and the one an app's video
        // depends on. opentok.dll and its three capturers reach an app only because
        // OpenTok.Client's build/OpenTok.Client.targets is imported there and copies them out of its
        // own package directory. NuGet's default is to keep a dependency's build assets private, and
        // under that default a consumer restores OpenTok.Client, never imports its targets, and
        // builds an app with no native payload at all — which fails at the first call into the SDK,
        // not at build.
        //
        // So the OpenTok.Client dependency must not exclude Build. Recorded in the nuspec as either
        // include="All" or the absence of an exclude, depending on how the reference is written.
        using var package = OpenPackage();

        var dependency = Dependencies(ReadNuspec(package), "OpenTok.Client");

        Assert.All(dependency, d => Assert.DoesNotContain("Build", Exclusions(d), StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Depends_on_the_vonage_windows_sdk()
    {
        // The whole point of the package is to render frames from OpenTok.Client. A consumer adding
        // this one should not also have to know to add that one — and, since the payload above is
        // deliberately not shipped here, this dependency is how the payload arrives at all.
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

    /// <summary>
    /// Every <c>&lt;dependency&gt;</c> element in <paramref name="nuspec"/> for <paramref name="id"/>
    /// — one per target framework group.
    /// </summary>
    private static IReadOnlyList<string> Dependencies(string nuspec, string id)
    {
        var elements = Regex
            .Matches(nuspec, "<dependency [^>]*>")
            .Select(m => m.Value)
            .Where(e => e.Contains($"id=\"{id}\"", StringComparison.Ordinal))
            .ToList();

        Assert.True(elements.Count > 0, $"the package does not depend on '{id}' at all.");

        return elements;
    }

    /// <summary>
    /// The asset kinds a <c>&lt;dependency&gt;</c> element keeps from flowing to a consumer: what
    /// its <c>exclude</c> attribute lists, less anything its <c>include</c> attribute puts back.
    /// </summary>
    private static IReadOnlyList<string> Exclusions(string dependency)
    {
        var excluded = Attribute(dependency, "exclude");
        var included = Attribute(dependency, "include");

        return included.Contains("All", StringComparer.OrdinalIgnoreCase)
            ? []
            : excluded.Except(included, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<string> Attribute(string element, string name)
    {
        var match = Regex.Match(element, $"{name}=\"(?<value>[^\"]*)\"");

        return match.Success
            ? match.Groups["value"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
    }

    /// <summary>
    /// Whether <paramref name="index"/> — the bytes of a .pri — contains <paramref name="file"/> as
    /// a name, in either encoding a string pool plausibly uses and whatever case MakePri wrote it in.
    /// </summary>
    private static bool Names(byte[] index, string file) =>
        Contains(index, Encoding.Unicode.GetBytes(file)) || Contains(index, Encoding.UTF8.GetBytes(file));

    private static bool Contains(byte[] haystack, byte[] needle) =>
        Lowered(haystack).AsSpan().IndexOf(Lowered(needle)) >= 0;

    private static byte[] Lowered(byte[] bytes) =>
        [.. bytes.Select(b => b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + ('a' - 'A')) : b)];

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
