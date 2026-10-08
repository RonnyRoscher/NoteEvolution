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

    private static void WriteModelFile(string userData, string modelId, string name)
    {
        var folder = Path.Combine(userData, "models", modelId);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, name), new byte[3]);
    }

    [Fact]
    public void DeleteAllExcept_LeavesOnlyKept()
    {
        using var dir = new TempDir();
        var store = new ModelStore(dir.Path);
        WriteModelFile(dir.Path, "a", "x.bin");
        WriteModelFile(dir.Path, "b", "y.bin");
        WriteModelFile(dir.Path, "keep", "z.bin");
        var keep = TestModel(("z.bin", new byte[3])) with { Id = "keep" };

        var errors = store.DeleteAllExcept(keep);

        Assert.Empty(errors);
        Assert.Equal([Path.Combine(dir.Path, "models", "keep")], Directory.GetDirectories(Path.Combine(dir.Path, "models")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "models", "keep", "z.bin")));
    }

    [Fact]
    public void DeleteAllExcept_MissingModelsFolder_IsFine()
    {
        using var dir = new TempDir();

        Assert.Empty(new ModelStore(dir.Path).DeleteAllExcept(TestModel()));
    }

    [Fact]
    public void DeleteAllExcept_LockedFile_ReportsError_DeletesTheRest()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Deleting a folder that holds an open file only fails on Windows.
        }

        using var dir = new TempDir();
        var store = new ModelStore(dir.Path);
        WriteModelFile(dir.Path, "a", "x.bin");
        WriteModelFile(dir.Path, "b", "y.bin");

        IReadOnlyList<string> errors;
        using (new FileStream(Path.Combine(dir.Path, "models", "a", "x.bin"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            errors = store.DeleteAllExcept(TestModel());
        }

        Assert.Single(errors);
        Assert.StartsWith("a: ", errors[0], StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "models", "b")));
    }

    [Fact]
    public void Delete_RemovesModelFolder_MissingIsFine()
    {
        using var dir = new TempDir();
        var store = new ModelStore(dir.Path);
        WriteModelFile(dir.Path, "test-model", "x.bin");
        var model = TestModel(("x.bin", new byte[3]));

        Assert.Null(store.Delete(model));
        Assert.False(Directory.Exists(store.DirectoryOf(model)));
        Assert.Null(store.Delete(model));
    }

    [Fact]
    public void Paths_LiveUnderModelsFolderOfUserData()
    {
        var store = new ModelStore(Path.Combine("x", "user"));
        var model = TestModel(("onnx/a.bin", new byte[1]));

        Assert.Equal(Path.Combine("x", "user", "models", "test-model"), store.DirectoryOf(model));
        Assert.Equal(Path.Combine("x", "user", "models", "test-model", "onnx", "a.bin"), store.PathOf(model, model.Files[0]));
    }
}
