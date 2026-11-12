namespace Cinder.Imaging;

/// <summary>
/// Thrown before anything is written when an acquisition or a container writer would have had
/// to replace files that already exist — the output image, the <c>.partial</c> working file of
/// an earlier run, or for E01 any segment of the target chain — and was not asked to
/// (<see cref="ImageJob.Overwrite"/>, <see cref="Ewf.EwfWriter.Options.Overwrite"/>,
/// <see cref="Aff4.Aff4Writer.Options.Overwrite"/>). The existing files are untouched.
/// </summary>
public sealed class OutputExistsException : IOException
{
    public OutputExistsException(string message, IReadOnlyList<string> existingPaths)
        : base(message)
    {
        ExistingPaths = existingPaths;
    }

    /// <summary>The files that are in the way.</summary>
    public IReadOnlyList<string> ExistingPaths { get; }
}
