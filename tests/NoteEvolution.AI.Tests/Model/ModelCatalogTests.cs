using NoteEvolution.AI.Model;

namespace NoteEvolution.AI.Tests.Model;

public class ModelCatalogTests
{
    private const string SentencePieceSha = "cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865";
    private const long SentencePieceSize = 5_069_051;

    [Fact]
    public void All_FourModelsInOrder_DefaultIsE5Small()
    {
        Assert.Equal(
            ["multilingual-e5-small-int8", "multilingual-e5-base-int8", "multilingual-e5-large-int8", "bge-m3-int8"],
            ModelCatalog.All.Select(m => m.Id));
        Assert.Same(ModelCatalog.All[0], ModelCatalog.Default);
        Assert.Same(ModelCatalog.E5Small, ModelCatalog.Default);
        Assert.Same(ModelCatalog.E5Base, ModelCatalog.All[1]);
        Assert.Same(ModelCatalog.E5Large, ModelCatalog.All[2]);
        Assert.Same(ModelCatalog.BgeM3, ModelCatalog.All[3]);
    }

    [Fact]
    public void DisplayNames_AsInSpec() =>
        Assert.Equal(["e5-small", "e5-base", "e5-large", "bge-m3"], ModelCatalog.All.Select(m => m.DisplayName));

    [Fact]
    public void Find_KnownAndUnknown()
    {
        foreach (var m in ModelCatalog.All)
            Assert.Same(m, ModelCatalog.Find(m.Id));

        Assert.Null(ModelCatalog.Find("x"));
        Assert.Null(ModelCatalog.Find(null));
    }

    public static TheoryData<string, string, string, string, long, int, Pooling, string> Pinned => new()
    {
        { "multilingual-e5-small-int8", "multilingual-e5-small", "761b726dd34fb83930e26aab4e9ac3899aa1fa78",
          "f80102d3f2a1229f387d3c81909990d8945513e347b0eab049f7de3c6f98c193", 118_308_185, 384, Pooling.Mean, "query: " },
        { "multilingual-e5-base-int8", "multilingual-e5-base", "1ec9243030a27d1a115d5c340572074c125b58b2",
          "df7a9a29309e3ad491e1783adf8baee710262cc06079c7cbab63c630277fac94", 278_647_662, 768, Pooling.Mean, "query: " },
        { "multilingual-e5-large-int8", "multilingual-e5-large", "00fc3aeb3dbb95842de2ac1961d33c6319acf57b",
          "0a8d65db9a36f810ba5da15249f13145fcdc7890e6656f1fd38cd8b7c4db1fca", 561_768_762, 1024, Pooling.Mean, "query: " },
        { "bge-m3-int8", "bge-m3", "4de13258303883538bd53b696b452bf8099f0858",
          "0826f8c1ab9edf1801db86c61919d4d108e8bfc0b809ec823ad366882ff0b77d", 569_694_530, 1024, Pooling.Cls, "" },
    };

    [Theory]
    [MemberData(nameof(Pinned))]
    public void Entries_PinnedValuesFromSpec(
        string id, string repo, string revision, string onnxSha, long onnxSize, int dimensions, Pooling pooling, string queryPrefix)
    {
        var m = ModelCatalog.Find(id)!;

        Assert.NotNull(m);
        Assert.Equal(dimensions, m.Dimensions);
        Assert.Equal(512, m.MaxTokens);
        Assert.Equal(pooling, m.Pooling);
        Assert.Equal(queryPrefix, m.QueryPrefix);
        Assert.Equal(pooling == Pooling.Mean ? "passage: " : "", m.PassagePrefix);
        Assert.Equal(["onnx/model_quantized.onnx", "sentencepiece.bpe.model"], m.Files.Select(f => f.RelativePath));

        Assert.Equal(onnxSha, m.Files[0].Sha256);
        Assert.Equal(onnxSize, m.Files[0].Size);
        Assert.Equal(SentencePieceSha, m.Files[1].Sha256);
        Assert.Equal(SentencePieceSize, m.Files[1].Size);

        foreach (var f in m.Files)
        {
            Assert.Equal($"https://huggingface.co/Xenova/{repo}/resolve/{revision}/{f.RelativePath}", f.Url);
            Assert.DoesNotContain("/main/", f.Url);
        }
    }

    [Fact]
    public void MemoryEstimate_TwiceLargestFilePlus150MB()
    {
        foreach (var m in ModelCatalog.All)
            Assert.Equal(2 * m.Files.Max(f => f.Size) + 150_000_000, m.MemoryEstimate);

        Assert.Equal([0.4, 0.7, 1.3, 1.3], ModelCatalog.All.Select(m => Math.Round(m.MemoryEstimate / 1e9, 1)));
    }

    [Fact]
    public void Ids_AndUrls_Unique()
    {
        Assert.Equal(ModelCatalog.All.Count, ModelCatalog.All.Select(m => m.Id).Distinct().Count());
        var urls = ModelCatalog.All.SelectMany(m => m.Files).Select(f => f.Url).ToList();
        Assert.Equal(urls.Count, urls.Distinct().Count());
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
