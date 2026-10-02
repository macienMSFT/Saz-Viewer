using System.IO.Compression;
using System.Text;
using ICSharpCode.SharpZipLib.Zip;
using SazViewer.Core;

namespace SazViewer.App.Tests;

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SazViewerAppTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal static class TestCaptures
{
    public const string Password = "correct horse battery staple";

    public static (string Name, byte[] Content)[] Entries() =>
    [
        ("raw/1_c.txt", Bytes(
            "GET /api/items?access_token=abc123secret HTTP/1.1\r\nHost: example.test\r\n"
            + "Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2lnbmF0dXJl\r\n"
            + "Cookie: session=topsecretcookie\r\n\r\n")),
        ("raw/1_s.txt", Bytes(
            "HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\n"
            + "Set-Cookie: session=newsecret; HttpOnly\r\n\r\n"
            + "{\"items\":[1,2,3],\"password\":\"hunter2\"}")),
        ("raw/1_m.xml", Bytes(
            "<Session><SessionTimers ClientBeginRequest=\"2024-05-01T12:00:00Z\" ClientDoneResponse=\"2024-05-01T12:00:01Z\"/></Session>")),
        ("raw/2_c.txt", Bytes(
            "POST /login HTTP/1.1\r\nHost: example.test\r\nContent-Type: application/x-www-form-urlencoded\r\n\r\n"
            + "user=alice&password=hunter2")),
        ("raw/2_s.txt", Bytes(
            "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic realm=\"test\"\r\nContent-Type: text/html\r\n\r\n"
            + "<html><body><h1>Denied</h1></body></html>")),
        ("raw/2_m.xml", Bytes(
            "<Session><SessionTimers ClientBeginRequest=\"2024-05-01T12:00:02Z\"/></Session>"))
    ];

    public static string WritePlain(string path)
    {
        using var file = System.IO.File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var (name, content) in Entries())
        {
            using var output = archive.CreateEntry(name, CompressionLevel.Fastest).Open();
            output.Write(content);
        }
        return path;
    }

    /// <summary>Writes a plain capture with <paramref name="sessions"/> simple GET sessions.</summary>
    public static string WriteSimple(string path, int sessions)
    {
        using var file = System.IO.File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        for (var i = 1; i <= sessions; i++)
        {
            using (var request = archive.CreateEntry($"raw/{i}_c.txt", CompressionLevel.Fastest).Open())
            {
                request.Write(Bytes($"GET /item/{i} HTTP/1.1\r\nHost: example.test\r\n\r\n"));
            }
            using var response = archive.CreateEntry($"raw/{i}_s.txt", CompressionLevel.Fastest).Open();
            response.Write(Bytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\n\r\nitem {i}"));
        }
        return path;
    }

    public static string WriteEncrypted(string path, string password = Password)
    {
        using var file = System.IO.File.Create(path);
        using var archive = new ZipOutputStream(file) { IsStreamOwner = false, UseZip64 = UseZip64.Off, Password = password };
        foreach (var (name, content) in Entries())
        {
            archive.PutNextEntry(new ZipEntry(name) { CompressionMethod = CompressionMethod.Deflated, AESKeySize = 256 });
            archive.Write(content);
            archive.CloseEntry();
        }
        archive.Finish();
        return path;
    }

    private static byte[] Bytes(string value) => Encoding.ASCII.GetBytes(value);
}

/// <summary>Returns queued passwords; counts requests; null when the queue is empty (cancel).</summary>
internal sealed class QueuePasswordProvider(params string[] passwords) : ISazPasswordProvider
{
    private readonly Queue<string> queue = new(passwords);

    public List<SazPasswordRequest> Requests { get; } = [];

    public List<char[]> Returned { get; } = [];

    public int MaximumAttempts => 3;

    public char[]? GetPassword(SazPasswordRequest request)
    {
        Requests.Add(request);
        if (queue.Count == 0)
        {
            return null;
        }
        var value = queue.Dequeue().ToCharArray();
        Returned.Add(value);
        return value;
    }
}
