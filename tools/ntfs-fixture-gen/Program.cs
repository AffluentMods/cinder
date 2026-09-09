using System.IO.Compression;
using System.Text;
using DiscUtils;
using DiscUtils.Ntfs;

// Builds tests/fixtures/ntfs-walker-fixture.img.gz for DiscUtilsWalkerTests.
//
// Windows only: DiscUtils formats NTFS through System.Security.Principal.SecurityIdentifier,
// which throws PlatformNotSupportedException everywhere else. The walker under test only
// reads, so the tests run on every OS against this image.
//
// Usage (from the repo root):  dotnet run --project tools/ntfs-fixture-gen -- tests/fixtures
//
// Layout must stay in sync with the assertions in DiscUtilsWalkerTests.

var outDir = args.Length > 0 ? args[0] : "tests/fixtures";
const long size = 8L << 20;
const int recordSize = 1024;          // DiscUtils' MFT record size; verified below via the FILE magic

var image = new MemoryStream();
image.SetLength(size);
var geo = Geometry.FromCapacity(size);
long deletedRecordOffset;
using (var fs = NtfsFileSystem.Format(image, "CINDER", geo, 0, geo.TotalSectorsLong))
{
    fs.CreateDirectory(@"Users\alice");
    Write(fs, @"Users\alice\notes.txt", Encoding.ASCII.GetBytes("hello evidence"));   // 14 bytes
    Write(fs, @"readme.md", new byte[1234]);
    Write(fs, @"kernel32.dll", Encoding.ASCII.GetBytes("known good bytes"));           // hash-set fixture
    Write(fs, @"unknown.bin", [1, 2, 3]);
    Write(fs, @"big.bin", new byte[100_000]);                                          // over a 50 KB hash limit
    for (int i = 0; i < 30; i++)
    {
        Write(fs, $"f{i:D2}.txt", [(byte)i]);
    }

    // Deleted the way Windows does it: clear the in-use bit, keep every attribute.
    Write(fs, @"secret-plans.docx", new byte[4096]);
    var index = fs.GetFileId(@"secret-plans.docx") & 0xFFFF_FFFF_FFFFL;
    var mftFirstCluster = fs.PathToClusters(@"$MFT")[0].Offset;
    deletedRecordOffset = fs.ClusterToOffset(mftFirstCluster) + index * recordSize;

    // Deleted the way DiscUtils does it: the record is reset, nothing survives.
    Write(fs, @"gone.bin", new byte[512]);
    fs.DeleteFile(@"gone.bin");
}

var bytes = image.GetBuffer();
if (Encoding.ASCII.GetString(bytes, (int)deletedRecordOffset, 4) != "FILE")
{
    throw new InvalidOperationException("MFT record offset did not land on a FILE record — record size assumption is wrong.");
}
bytes[deletedRecordOffset + 0x16] &= 0xFE;   // MFT_RECORD_IN_USE = 0x0001

Directory.CreateDirectory(outDir);
var gz = Path.Combine(outDir, "ntfs-walker-fixture.img.gz");
using (var o = File.Create(gz))
using (var z = new GZipStream(o, CompressionLevel.SmallestSize))
{
    z.Write(bytes, 0, (int)size);
}
Console.WriteLine($"wrote {gz} ({new FileInfo(gz).Length:N0} bytes gz, {size:N0} raw); deleted record at {deletedRecordOffset}");

static void Write(NtfsFileSystem fs, string path, byte[] content)
{
    using var s = fs.OpenFile(path, FileMode.Create, FileAccess.ReadWrite);
    s.Write(content, 0, content.Length);
}
