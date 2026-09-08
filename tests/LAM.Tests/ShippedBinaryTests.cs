using System.Text;
using System.Text.RegularExpressions;
using LAM.Core.Model;
using Xunit;

namespace LAM.Tests;

/// <summary>
/// Ship-gates on the compiled assemblies themselves, rather than on the source that produced them.
///
/// Every other test here reads code. These read the bytes a user actually downloads, because the two
/// can differ: the compiler stamps things into an assembly that appear nowhere in any source file —
/// the CodeView debug path, the SourceLink map — and a string literal lands as UTF-16, so a habit of
/// grepping source, or of grepping a binary for ASCII, proves nothing about what shipped.
/// </summary>
public sealed class ShippedBinaryTests
{
    /// <summary>A shipped assembly, read as bytes. LAM.Core is the one carrying the Riot code.</summary>
    private static byte[] ShippedAssembly()
    {
        var path = typeof(AppSettings).Assembly.Location;

        // Asserted, not skipped. Under `dotnet test` the assembly always has a file on disk, so a
        // missing one means the gate is not scanning what it thinks it is — which must fail loudly
        // rather than pass on zero bytes.
        Assert.True(!string.IsNullOrEmpty(path) && File.Exists(path),
            "LAM.Core has no file on disk, so this gate would be scanning nothing.");

        return File.ReadAllBytes(path);
    }

    /// <summary>Both encodings a .NET assembly can hold a string in: UTF-8 for metadata, UTF-16 for literals.</summary>
    private static bool Contains(byte[] haystack, string needle) =>
        Find(haystack, Encoding.UTF8.GetBytes(needle)) || Find(haystack, Encoding.Unicode.GetBytes(needle));

    private static bool Find(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            var j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j]) j++;
            if (j == needle.Length) return true;
        }

        return false;
    }

    /// <summary>
    /// The builder's Windows account name must not ship.
    ///
    /// Without <c>PathMap</c> (Directory.Build.props) the compiler writes the absolute build path into
    /// the debug directory and the SourceLink map, so the published .exe tells every user who built
    /// it and where. Not a secret — but personal information nobody agreed to publish, and free to
    /// remove. If this fails, PathMap has been dropped or a configuration is bypassing it.
    /// </summary>
    [Fact]
    public void No_build_machine_path_is_stamped_into_a_shipped_assembly()
    {
        var bytes = ShippedAssembly();

        Assert.False(Contains(bytes, @"C:\Users\"), "A Windows user profile path is embedded in the built assembly.");
        Assert.False(Contains(bytes, @"/Users/"), "A macOS/Linux home path is embedded in the built assembly.");
        Assert.False(Contains(bytes, @"C:\Windows\"), "A machine-specific system path is embedded in the built assembly.");
    }

    /// <summary>
    /// No real Riot API key may ever be compiled in.
    ///
    /// The key belongs to the user and lives only inside the encrypted vault. The prefix "RGAPI-"
    /// appearing in an error message is fine and expected; a full 42-character key is not, and would
    /// mean somebody pasted a live key into a source file or a test fixture.
    /// </summary>
    [Fact]
    public void No_real_riot_api_key_is_compiled_into_a_shipped_assembly()
    {
        var bytes = ShippedAssembly();

        // Both encodings, one pass each: the literal chars, then the same chars NUL-interleaved.
        var utf8 = Encoding.UTF8.GetString(bytes);
        var utf16 = Encoding.Unicode.GetString(bytes, 0, bytes.Length - (bytes.Length % 2));

        const string keyShape = @"RGAPI-[0-9A-Za-z]{8}-[0-9A-Za-z]{4}-[0-9A-Za-z]{4}-[0-9A-Za-z]{4}-[0-9A-Za-z]{12}";

        Assert.DoesNotMatch(keyShape, utf8);
        Assert.DoesNotMatch(keyShape, utf16);
    }

    /// <summary>
    /// The regex above has to be able to fail, or it proves nothing.
    ///
    /// Written because the suite has previously carried an assertion that could not fail — and an
    /// always-green ship-gate is worse than none, since it is trusted.
    /// </summary>
    [Fact]
    public void The_key_shape_this_gate_looks_for_actually_matches_a_key()
    {
        const string keyShape = @"RGAPI-[0-9A-Za-z]{8}-[0-9A-Za-z]{4}-[0-9A-Za-z]{4}-[0-9A-Za-z]{4}-[0-9A-Za-z]{12}";

        Assert.Matches(keyShape, "RGAPI-12ab34cd-5e6f-7a8b-9c0d-1e2f3a4b5c6d");
        Assert.DoesNotMatch(keyShape, "RGAPI-test");
        Assert.DoesNotMatch(keyShape, "should start with RGAPI- and be 42 characters.");
    }
}
