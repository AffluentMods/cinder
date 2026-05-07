namespace Cinder.Hex;

/// <summary>One byte-level edit, used to power the undo/redo stack.</summary>
public sealed record HexEditOperation(long Offset, byte[] OriginalBytes, byte[] NewBytes);

public sealed class HexEditHistory
{
    private readonly Stack<HexEditOperation> _undo = new();
    private readonly Stack<HexEditOperation> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Push(HexEditOperation op)
    {
        _undo.Push(op);
        _redo.Clear();
    }

    public HexEditOperation? Undo()
    {
        if (!_undo.TryPop(out var op))
        {
            return null;
        }
        _redo.Push(op);
        return op;
    }

    public HexEditOperation? Redo()
    {
        if (!_redo.TryPop(out var op))
        {
            return null;
        }
        _undo.Push(op);
        return op;
    }
}
