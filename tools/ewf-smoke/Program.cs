using System.Security.Cryptography;
using Cinder.Imaging.Ewf;

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: ewf-smoke <path-to-.E01>");
    return 1;
}

using var ewf = EwfReader.Open(args[0]);
Console.WriteLine($"=== EWF metadata ===");
Console.WriteLine($"  case description     = {ewf.CaseDescription}");
Console.WriteLine($"  media size           = {ewf.MediaSize:N0} bytes");
Console.WriteLine($"  sectors              = {ewf.NumberOfSectors:N0}");
Console.WriteLine($"  bytes per sector     = {ewf.BytesPerSector}");
Console.WriteLine($"  chunks               = {ewf.NumberOfChunks:N0}");
Console.WriteLine($"  recorded MD5         = {ewf.RecordedMd5}");
Console.WriteLine($"  recorded SHA1        = {ewf.RecordedSha1}");

Console.WriteLine($"\n=== Reading raw disk through EwfStream ===");
using var stream = ewf.OpenStream();
Console.WriteLine($"  Length               = {stream.Length:N0} bytes");

// Verify by hashing the entire raw disk and comparing to the recorded SHA1.
Console.Write($"  Hashing entire disk… ");
var sha1 = SHA1.Create();
var buf = new byte[4 * 1024 * 1024];
long total = 0;
int n;
while ((n = stream.Read(buf, 0, buf.Length)) > 0)
{
    sha1.TransformBlock(buf, 0, n, null, 0);
    total += n;
}
sha1.TransformFinalBlock([], 0, 0);
var computed = Convert.ToHexString(sha1.Hash!).ToLowerInvariant();
Console.WriteLine($"done ({total:N0} bytes)");
Console.WriteLine($"  computed SHA1        = {computed}");
Console.WriteLine($"  matches recorded?    = {computed.Equals(ewf.RecordedSha1, StringComparison.OrdinalIgnoreCase)}");

return 0;
