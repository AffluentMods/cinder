namespace Cinder.Native;

/// <summary>Software write-blocker. Implementations differ by OS (kernel filter vs blockdev).</summary>
public interface IWriteBlocker
{
    bool IsActive { get; }
    bool TryEngage();
    bool TryDisengage();
}
