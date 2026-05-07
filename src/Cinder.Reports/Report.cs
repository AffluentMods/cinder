using Cinder.Artifacts;

namespace Cinder.Reports;

/// <summary>An exhibit attached to a report — usually one bookmark, an image, a hex region, or
/// a parser-output table.</summary>
public sealed record Exhibit(
    string Id,                 // generated, e.g. "EX-0001"
    string Title,
    ExhibitKind Kind,
    string? Description,
    string? FilePath,          // when Kind = File or Image
    long? FileSize,
    string? Sha256,
    string? Examiner,
    DateTimeOffset CapturedUtc,
    IReadOnlyList<IArtifact>? Artifacts = null,
    IReadOnlyDictionary<string, string>? Properties = null);

public enum ExhibitKind { File, Image, Snippet, ArtifactSet, Hash, Note, Screenshot }

/// <summary>One section in a report — a header + free-form Markdown body + zero or more exhibits.</summary>
public sealed record ReportSection(
    string Title,
    string MarkdownBody,
    IReadOnlyList<Exhibit> Exhibits);

/// <summary>The report being built. Snapshotted to JSON for round-trip into the case file.</summary>
public sealed record Report(
    Guid Id,
    string CaseName,
    string Examiner,
    string Title,
    DateTimeOffset CreatedUtc,
    string TemplateId,
    IReadOnlyList<ReportSection> Sections,
    IReadOnlyDictionary<string, string>? Metadata = null);

public static class ReportTemplates
{
    public static IReadOnlyList<ReportTemplate> All { get; } =
    [
        new("expert-witness", "Expert Witness Report",
            "Court-ready format with declarations, qualifications, methodology, findings, and exhibits.",
            ["Declarations", "Qualifications", "Scope", "Methodology", "Findings", "Conclusions", "Exhibits"]),
        new("incident-response", "Incident Response Report",
            "DFIR engagement format with executive summary, timeline, root cause, and containment.",
            ["Executive Summary", "Engagement Timeline", "Root Cause", "Indicators of Compromise", "Containment", "Recommendations", "Appendix"]),
        new("internal-audit", "Internal Audit Report",
            "Lightweight format for internal investigations and policy violations.",
            ["Background", "Findings", "Evidence", "Conclusions", "Recommendations"]),
        new("plain", "Plain Markdown",
            "Free-form Markdown with no enforced structure. Useful for personal notes.",
            []),
    ];
}

public sealed record ReportTemplate(string Id, string Name, string Description, IReadOnlyList<string> DefaultSections);
