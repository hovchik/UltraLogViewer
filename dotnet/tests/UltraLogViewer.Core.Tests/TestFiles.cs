using System.Text;

namespace UltraLogViewer.Core.Tests;

internal static class TestFiles
{
    private static readonly string Dir = Directory.CreateTempSubdirectory("ulv-test-").FullName;

    public static string Write(string name, string content) => Write(name, Encoding.UTF8.GetBytes(content));

    public static string Write(string name, byte[] content)
    {
        var p = Path.Combine(Dir, $"{Guid.NewGuid():N}-{name}");
        File.WriteAllBytes(p, content);
        return p;
    }

    public static long Local(int y, int mo, int d, int h, int mi, int s, int ms = 0) =>
        new DateTimeOffset(new DateTime(y, mo, d, h, mi, s, ms, DateTimeKind.Local)).ToUnixTimeMilliseconds();

    public const string Log4j =
        "2024-05-01 10:00:00,001 [main] INFO  com.acme.App - Starting app\n" +
        "2024-05-01 10:00:01,002 [main] DEBUG com.acme.Db - Connecting to db\n" +
        "2024-05-01 10:00:02,003 [worker-1] WARN  com.acme.Cache - Cache miss ratio high\n" +
        "2024-05-01 10:00:03,004 [worker-2] ERROR com.acme.Db - Query failed\n" +
        "java.sql.SQLException: timeout\n" +
        "\tat com.acme.Db.query(Db.java:42)\n" +
        "\tat com.acme.App.main(App.java:10)\n" +
        "2024-05-01 10:00:04,005 [main] INFO  com.acme.App - Retrying\n" +
        "2024-05-01 10:00:05,006 [main] FATAL com.acme.App - Giving up\n";
}
