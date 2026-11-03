using System.IO.Compression;

namespace UltraLogViewer.Core;

/// <summary>
/// Compressed inputs: gzip, bzip2 and zip archives. A zip opens as a merged view of its text members, each
/// addressed as "archive.zip::member/path.log" so it can be opened, saved in a workspace and reopened like a file.
/// </summary>
public static class Archives
{
    public const string MemberSeparator = "::";

    /// <summary>Member extensions that are never logs (skipped when a zip is expanded).</summary>
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".svg", ".webp", ".pdf", ".zip", ".gz", ".tgz", ".bz2", ".xz", ".7z", ".rar", ".jar",
        ".war", ".class", ".exe", ".dll", ".so", ".dylib", ".bin", ".dat", ".db", ".sqlite", ".hprof", ".dmp", ".mdmp", ".core", ".pyc", ".woff", ".woff2", ".ttf",
    };

    /// <summary>Splits "archive.zip::member" into its parts; null when the path is a plain file.</summary>
    public static (string Archive, string Member)? SplitMember(string path)
    {
        var i = path.IndexOf(MemberSeparator, StringComparison.Ordinal);
        if (i <= 0 || File.Exists(path)) return null; // a plain file may have "::" in its name
        var archive = path[..i];
        return File.Exists(archive) ? (archive, path[(i + MemberSeparator.Length)..]) : null;
    }

    public static string MemberPath(string archive, string member) => archive + MemberSeparator + member;

    /// <summary>Display name of a path: the file name, or the member's file name for a zip member.</summary>
    public static string DisplayName(string path)
    {
        var m = SplitMember(path);
        var name = m is { } x ? x.Member : path;
        var slash = name.LastIndexOfAny(['/', '\\']);
        return slash >= 0 ? name[(slash + 1)..] : name;
    }

    public static bool IsZip(ReadOnlySpan<byte> head) => head.Length >= 4 && head[0] == 'P' && head[1] == 'K' && head[2] == 3 && head[3] == 4;
    public static bool IsGzip(ReadOnlySpan<byte> head) => head.Length >= 2 && head[0] == 0x1f && head[1] == 0x8b;

    /// <summary>Text members of a zip in name order (directories, empty and binary members left out).</summary>
    public static List<string> LogMembers(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        return archive.Entries.Where(e => e.Length > 0 && !e.FullName.EndsWith('/') && !e.FullName.StartsWith("__MACOSX/", StringComparison.Ordinal)
                && !BinaryExtensions.Contains(Path.GetExtension(e.Name)))
            .Select(e => e.FullName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Replaces each zip file by its text members, so a support bundle opens as one merged timeline.</summary>
    public static string[] Expand(IEnumerable<string> paths)
    {
        var output = new List<string>();
        foreach (var p in paths)
        {
            if (SplitMember(p) is null && IsZipFile(p))
            {
                var members = LogMembers(p);
                if (members.Count == 0) throw new InvalidDataException($"{Path.GetFileName(p)} has no text files.");
                output.AddRange(members.Select(m => MemberPath(p, m)));
            }
            else output.Add(p);
        }
        return [.. output];
    }

    public static bool IsZipFile(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> head = stackalloc byte[4];
            return fs.Read(head) == 4 && IsZip(head);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Opens a decompressing stream over a compressed input, or returns null for a plain file. <paramref name="total"/>
    /// and <paramref name="position"/> report progress in compressed bytes (or uncompressed for a zip member).
    /// </summary>
    public static Stream? OpenDecompressed(string path, out string kind, out long total, out Func<long> position)
    {
        if (SplitMember(path) is { } m)
        {
            var archive = ZipFile.OpenRead(m.Archive);
            try
            {
                var entry = archive.GetEntry(m.Member) ?? throw new FileNotFoundException($"{m.Member} is not in {Path.GetFileName(m.Archive)}.");
                kind = "zip";
                total = entry.Length;
                var s = new OwningStream(entry.Open(), archive);
                position = () => s.Consumed;
                return s;
            }
            catch
            {
                archive.Dispose();
                throw;
            }
        }
        var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        Span<byte> head = stackalloc byte[4];
        var n = input.Read(head);
        input.Position = 0;
        total = input.Length;
        position = () => input.Position;
        if (IsGzip(head[..n]))
        {
            kind = "gzip";
            return new GZipStream(input, CompressionMode.Decompress);
        }
        if (Bzip2Stream.IsBzip2(head[..n]))
        {
            kind = "bzip2";
            var bz = new Bzip2Stream(input);
            position = () => bz.CompressedPosition;
            return bz;
        }
        if (IsZip(head[..n]))
        {
            // A zip opened directly (not expanded): read its first text member.
            input.Dispose();
            var first = LogMembers(path).FirstOrDefault() ?? throw new InvalidDataException($"{Path.GetFileName(path)} has no text files.");
            return OpenDecompressed(MemberPath(path, first), out kind, out total, out position);
        }
        input.Dispose();
        kind = "";
        return null;
    }

    /// <summary>A member stream that also closes its archive and counts the bytes read.</summary>
    private sealed class OwningStream(Stream inner, IDisposable owner) : Stream
    {
        public long Consumed;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Consumed; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var n = inner.Read(buffer);
            Consumed += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                owner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
