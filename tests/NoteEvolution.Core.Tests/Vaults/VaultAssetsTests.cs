using NoteEvolution.Core.Vaults;

namespace NoteEvolution.Core.Tests.Vaults;

public sealed class VaultAssetsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ne-assets-" + Guid.NewGuid().ToString("N"));
    private readonly string _vault;
    private readonly string _book;

    public VaultAssetsTests()
    {
        _vault = Path.Combine(_root, "vault");
        Directory.CreateDirectory(Path.Combine(_vault, "pages"));
        _book = Path.Combine(_vault, "pages", "Buch.md");
        File.WriteAllText(_book, "- x");
    }

    public void Dispose() => Directory.Delete(_root, true);

    private static readonly byte[] One = [1, 2, 3];
    private static readonly byte[] Two = [4, 5, 6];

    [Fact]
    public void Import_CreatesAssetsAndReturnsRelativePath()
    {
        var path = VaultAssets.Import(_vault, _book, "Mein Bild.png", One);

        Assert.Equal("../assets/Mein-Bild.png", path);
        Assert.Equal(One, File.ReadAllBytes(Path.Combine(_vault, "assets", "Mein-Bild.png")));
    }

    [Fact]
    public void Import_SameBytes_ReusesFile()
    {
        var first = VaultAssets.Import(_vault, _book, "Mein Bild.png", One);
        var second = VaultAssets.Import(_vault, _book, "Mein Bild.png", One);

        Assert.Equal(first, second);
        Assert.Single(Directory.GetFiles(Path.Combine(_vault, "assets")));
    }

    [Fact]
    public void Import_OtherBytes_GetsNumberedName()
    {
        VaultAssets.Import(_vault, _book, "Mein Bild.png", One);
        var second = VaultAssets.Import(_vault, _book, "Mein Bild.png", Two);

        Assert.Equal("../assets/Mein-Bild-2.png", second);
        Assert.Equal(One, File.ReadAllBytes(Path.Combine(_vault, "assets", "Mein-Bild.png")));
        Assert.Equal(Two, File.ReadAllBytes(Path.Combine(_vault, "assets", "Mein-Bild-2.png")));
        Assert.Equal("../assets/Mein-Bild-3.png", VaultAssets.Import(_vault, _book, "Mein Bild.png", [7]));
    }

    [Fact]
    public void Import_EmptyStem_UsesBild() =>
        Assert.Equal("../assets/bild.png", VaultAssets.Import(_vault, _book, ".png", One));

    [Fact]
    public void Import_OtherExtension_RefusedAsFormat()
    {
        var ex = Assert.Throws<AssetRefusedException>(() => VaultAssets.Import(_vault, _book, "a.txt", One));

        Assert.Equal("format", ex.Message);
    }

    [Fact]
    public void Import_TooLargeOrEmpty_RefusedAsSize()
    {
        var big = Assert.Throws<AssetRefusedException>(
            () => VaultAssets.Import(_vault, _book, "a.png", new byte[VaultAssets.MaxBytes + 1]));
        var empty = Assert.Throws<AssetRefusedException>(() => VaultAssets.Import(_vault, _book, "a.png", []));

        Assert.Equal("size", big.Message);
        Assert.Equal("size", empty.Message);
    }

    [Fact]
    public void IsImageName_And_MimeOf()
    {
        Assert.True(VaultAssets.IsImageName("A.PNG"));
        Assert.True(VaultAssets.IsImageName("a.jpeg"));
        Assert.False(VaultAssets.IsImageName("a.txt"));
        Assert.False(VaultAssets.IsImageName("png"));
        Assert.Equal("image/jpeg", VaultAssets.MimeOf("a.JPG"));
        Assert.Equal("image/png", VaultAssets.MimeOf("a.png"));
        Assert.Equal("image/gif", VaultAssets.MimeOf("a.gif"));
        Assert.Equal("image/webp", VaultAssets.MimeOf("a.webp"));
        Assert.Equal("image/bmp", VaultAssets.MimeOf("a.bmp"));
    }

    [Fact]
    public void Resolve_FindsImportedFile_AlsoPercentEncoded()
    {
        var path = VaultAssets.Import(_vault, _book, "Mein Bild.png", One);
        var expected = Path.GetFullPath(Path.Combine(_vault, "assets", "Mein-Bild.png"));

        Assert.Equal(expected, VaultAssets.Resolve(_vault, _book, path));
        Assert.Equal(expected, VaultAssets.Resolve(_vault, _book, "../assets/Mein%2DBild.png"));
    }

    [Fact]
    public void Resolve_PercentEncodedSpace()
    {
        Directory.CreateDirectory(Path.Combine(_vault, "assets"));
        var file = Path.Combine(_vault, "assets", "a b.png");
        File.WriteAllBytes(file, One);

        Assert.Equal(Path.GetFullPath(file), VaultAssets.Resolve(_vault, _book, "../assets/a%20b.png"));
        Assert.Equal(Path.GetFullPath(file), VaultAssets.Resolve(_vault, _book, "../assets/a b.png"));
    }

    [Fact]
    public void Resolve_OutsideRemoteMissingOrNotImage_ReturnsNull()
    {
        File.WriteAllBytes(Path.Combine(_root, "outside.png"), One);
        Directory.CreateDirectory(Path.Combine(_vault, "assets"));
        File.WriteAllText(Path.Combine(_vault, "assets", "notes.txt"), "x");

        Assert.Null(VaultAssets.Resolve(_vault, _book, "../../outside.png"));
        Assert.Null(VaultAssets.Resolve(_vault, _book, Path.Combine(_root, "outside.png")));
        Assert.Null(VaultAssets.Resolve(_vault, _book, "https://example.org/a.png"));
        Assert.Null(VaultAssets.Resolve(_vault, _book, "../assets/missing.png"));
        Assert.Null(VaultAssets.Resolve(_vault, _book, "../assets/notes.txt"));
    }

    [Fact]
    public void Resolve_SiblingDirectoryWithTheVaultNamePrefix_ReturnsNull()
    {
        var other = Path.Combine(_root, "vault-other");
        Directory.CreateDirectory(other);
        File.WriteAllBytes(Path.Combine(other, "x.png"), One);

        Assert.Null(VaultAssets.Resolve(_vault, _book, "../../vault-other/x.png"));
    }

    [Fact]
    public void Resolve_PercentEncodedEscape_ReturnsNull()
    {
        File.WriteAllBytes(Path.Combine(_root, "outside.png"), One);

        Assert.Null(VaultAssets.Resolve(_vault, _book, "..%2F..%2Foutside.png"));
    }
}
