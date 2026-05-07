namespace Cinder.Core.Custody;

/// <summary>
/// Canonical action verbs that go into the chain-of-custody log. New verbs may be added; existing
/// verbs must never be renamed (they're hashed into the chain).
/// </summary>
public static class CustodyAction
{
    public const string CaseCreated = "case.created";
    public const string CaseOpened = "case.opened";
    public const string EvidenceHashed = "evidence.hashed";
    public const string EvidenceImaged = "evidence.imaged";
    public const string EvidenceMounted = "evidence.mounted";
    public const string EvidenceUnmounted = "evidence.unmounted";
    public const string ParserRan = "parser.ran";
    public const string ReportExported = "report.exported";
    public const string Annotation = "annotation";
}
