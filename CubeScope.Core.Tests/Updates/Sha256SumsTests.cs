using CubeScope.Core.Updates;

namespace CubeScope.Core.Tests.Updates;

/// <summary>Finding the expected hash of a file in a <c>SHA256SUMS</c> listing.</summary>
public sealed class Sha256SumsTests
{
    private const string Hash = "bee805430933bdf830ed5f168e2484309c285eb9ef941f8b9d780af4164c6b07";

    [Theory]
    // Binary marker, no end of line.
    [InlineData(Hash + " *cubescope.exe")]
    // Text-mode form of sha256sum: what release.yml writes (sha256sum cubescope.exe > ….sha256).
    [InlineData(Hash + "  cubescope.exe\n")]
    [InlineData(Hash + "  cubescope.exe")]
    [InlineData(Hash + " *cubescope.exe\n")]
    [InlineData(Hash + " *cubescope.exe\r\n")]
    public void The_hash_of_the_file_is_found(string content)
    {
        Assert.Equal(Hash, Sha256Sums.Find(content, "cubescope.exe"));
    }

    [Fact]
    public void An_upper_case_hash_is_returned_in_lower_case()
    {
        Assert.Equal(Hash, Sha256Sums.Find(Hash.ToUpperInvariant() + " *cubescope.exe", "cubescope.exe"));
    }

    [Fact]
    public void The_right_line_is_picked_among_several()
    {
        var other = new string('a', 64);
        var content = $"{other} *cubescope.exe.bak\r\n{other} *Other.zip\r\n{Hash} *cubescope.exe\r\n";

        Assert.Equal(Hash, Sha256Sums.Find(content, "cubescope.exe"));
    }

    [Theory]
    [InlineData("cubescope.exe")]
    [InlineData("PivotScope.zip")]
    public void Each_name_is_found_in_a_two_line_listing(string fileName)
    {
        var content = $"{Hash}  cubescope.exe\n{Hash}  PivotScope.zip\n";

        Assert.Equal(Hash, Sha256Sums.Find(content, fileName));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\r\n")]
    [InlineData("not a listing at all")]
    // A name that only starts or ends the same is another file.
    [InlineData(Hash + " *cubescope.exe.bak")]
    [InlineData(Hash + " *Mycubescope.exe")]
    // 63 characters.
    [InlineData("ee805430933bdf830ed5f168e2484309c285eb9ef941f8b9d780af4164c6b07 *cubescope.exe")]
    // 65 characters.
    [InlineData("0" + Hash + " *cubescope.exe")]
    // Not hexadecimal ('g').
    [InlineData("gee805430933bdf830ed5f168e2484309c285eb9ef941f8b9d780af4164c6b07 *cubescope.exe")]
    // No separator between hash and name.
    [InlineData(Hash + "cubescope.exe")]
    public void Anything_else_gives_nothing(string content)
    {
        Assert.Null(Sha256Sums.Find(content, "cubescope.exe"));
    }
}
