using NoteEvolution.Core.Links;

namespace NoteEvolution.Core.Tests.Links;

public class UndoManagerTests
{
    private sealed class Recording(string description, List<string> log) : IUndoAction
    {
        public string Description => description;

        public void Undo() => log.Add(description);
    }

    [Fact]
    public void Undo_RunsLatestAction_RaisesChanged()
    {
        var log = new List<string>();
        var undo = new UndoManager();
        var changes = 0;
        undo.Changed += () => changes++;
        Assert.False(undo.CanUndo);
        Assert.Null(undo.NextDescription);

        undo.Push(new Recording("eins", log));
        undo.Push(new Recording("zwei", log));
        Assert.Equal("zwei", undo.NextDescription);

        undo.Undo();

        Assert.Equal(["zwei"], log);
        Assert.Equal("eins", undo.NextDescription);
        Assert.Equal(3, changes);
    }

    [Fact]
    public void Push_KeepsAtMost50Actions()
    {
        var log = new List<string>();
        var undo = new UndoManager();
        for (var i = 1; i <= 51; i++)
        {
            undo.Push(new Recording(i.ToString(), log));
        }

        while (undo.CanUndo)
        {
            undo.Undo();
        }

        Assert.Equal(50, log.Count);
        Assert.Equal("51", log[0]);
        Assert.Equal("2", log[^1]);
    }

    [Fact]
    public void Undo_ActionThrows_ActionIsDropped()
    {
        var undo = new UndoManager();
        undo.Push(new Failing());

        Assert.Throws<IOException>(undo.Undo);
        Assert.False(undo.CanUndo);
    }

    private sealed class Failing : IUndoAction
    {
        public string Description => "kaputt";

        public void Undo() => throw new IOException("gesperrt");
    }
}
