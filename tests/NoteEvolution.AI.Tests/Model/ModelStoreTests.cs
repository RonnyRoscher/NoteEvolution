using NoteEvolution.AI.Model;
using NoteEvolution.TestSupport;

namespace NoteEvolution.AI.Tests.Model;

public class ModelStoreTests
{
    internal static ModelInfo TestModel(params (string Path, byte[] Data)[] files) =>
        new("test-model", "Test model",
            [.. files.Select(f => new ModelFile(f.Path, "https://example.test/" + f.Path, Hash(f.Data), f.Data.Length))],
            Dimensions: 4, MaxTokens: 8);

    internal static string Hash(byte[] data) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data));

    [Fact]
    public void IsInstalled_FalseForMissingOrWrongSize()
    {
        using var dir = new TempDir();
        var store = new ModelStore(dir.Path);
        var model = TestModel(("onnx/a.bin", new byte[10]), ("b.bin", new byte[5]));

        Assert.False(store.IsInstalled(model));

        Directory.CreateDirectory(Path.GetDirectoryName(store.PathOf(model, model.Files[0]))!);
        File.WriteAllBytes(store.PathOf(model, model.Files[0]), new byte[10]);
        Assert.False(store.IsInstalled(model));                       // second file missing

        File.WriteAllBytes(store.PathOf(model, model.Files[1]), new byte[4]);
        Assert.False(store.IsInstalled(model));                       // wrong size

        File.WriteAllBytes(store.PathOf(model, model.Files[1]), new byte[5]);
        Assert.True(store.IsInstalled(model));
    }

    [Fact]
    public void Paths_LiveUnderModelsFolderOfUserData()
    {
        var store = new ModelStore(Path.Combine("x", "user"));
        var model = TestModel(("onnx/a.bin", new byte[1]));

        Assert.Equal(Path.Combine("x", "user", "models", "test-model"), store.DirectoryOf(model));
        Assert.Equal(Path.Combine("x", "user", "models", "test-model", "onnx", "a.bin"), store.PathOf(model, model.Files[0]));
    }

    [Fact]
    public void Catalog_E5Small_PinnedRevisionAndHashes()
    {
        var m = ModelCatalog.E5Small;

        Assert.Equal("multilingual-e5-small-int8", m.Id);
        Assert.Equal(384, m.Dimensions);
        Assert.Equal(512, m.MaxTokens);
        Assert.Equal(["onnx/model_quantized.onnx", "sentencepiece.bpe.model"], m.Files.Select(f => f.RelativePath));
        foreach (var f in m.Files)
        {
            Assert.StartsWith("https://huggingface.co/Xenova/multilingual-e5-small/resolve/", f.Url);
            Assert.DoesNotContain("/main/", f.Url);
            Assert.EndsWith("/" + f.RelativePath, f.Url);
            Assert.Matches("^[0-9a-f]{64}$", f.Sha256);
            Assert.True(f.Size > 0);
        }
    }
}
