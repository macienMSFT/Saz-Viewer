using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using ICSharpCode.SharpZipLib.Zip;
using SazViewer.Cli;
using SazViewer.Core;

namespace SazViewer.Tests;

public sealed class EncryptedSazTests
{
    private const string TestPassword = "neutral test password";

    [Fact]
    public void ParsesAes256EncryptedSaz()
    {
        using var saz = EncryptedFixture(
            TestPassword,
            EncryptionKind.Aes256,
            HttpEntries());
        var provider = new SequencePasswordProvider(TestPassword);

        var report = new SazParser().Parse(saz, passwordProvider: provider);

        var session = Assert.Single(report.Sessions);
        Assert.Equal("GET", session.Method);
        Assert.Equal(200, session.StatusCode);
        Assert.Equal("encrypted response", session.Response!.Body.Preview);
        Assert.Single(provider.Requests);
    }

    [Fact]
    public void ParsesTraditionalZipCryptoAndMixedEntries()
    {
        using var saz = MixedFixture(TestPassword);

        var report = new SazParser().Parse(
            saz,
            passwordProvider: new SequencePasswordProvider(TestPassword));

        var session = Assert.Single(report.Sessions);
        Assert.Equal("GET", session.Method);
        Assert.Equal(200, session.StatusCode);
        Assert.NotNull(session.Timestamp);
    }

    [Fact]
    public void RetriesOnlyWrongPasswordsUpToProviderLimit()
    {
        using var saz = EncryptedFixture(
            TestPassword,
            EncryptionKind.Aes256,
            HttpEntries());
        var provider = new SequencePasswordProvider("wrong one", "wrong two", TestPassword);

        var report = new SazParser().Parse(saz, passwordProvider: provider);

        Assert.Single(report.Sessions);
        Assert.Equal(3, provider.Requests.Count);
        Assert.Equal([1, 2, 3], provider.Requests.Select(request => request.Attempt));
        Assert.Equal([false, true, true], provider.Requests.Select(request => request.PreviousPasswordRejected));
    }

    [Fact]
    public void RejectsWrongPasswordWithoutDisclosingIt()
    {
        const string rejectedPassword = "do-not-print-this-test-secret";
        using var saz = EncryptedFixture(
            TestPassword,
            EncryptionKind.Aes256,
            HttpEntries());

        var exception = Assert.Throws<SazAuthenticationException>(
            () => new SazParser().Parse(
                saz,
                passwordProvider: new SequencePasswordProvider(
                    rejectedPassword,
                    rejectedPassword,
                    rejectedPassword)));

        Assert.DoesNotContain(rejectedPassword, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RequiresProviderOnlyForEncryptedArchives()
    {
        using var encrypted = EncryptedFixture(
            TestPassword,
            EncryptionKind.Aes256,
            HttpEntries());
        Assert.Throws<SazPasswordRequiredException>(() => new SazParser().Parse(encrypted));

        using var plain = PlainFixture(HttpEntries());
        var provider = new SequencePasswordProvider(TestPassword);
        Assert.Single(new SazParser().Parse(plain, passwordProvider: provider).Sessions);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public void PreservesMeaningfulPasswordWhitespace()
    {
        const string password = "  spaced password  ";
        using var saz = EncryptedFixture(password, EncryptionKind.Aes256, HttpEntries());

        var report = new SazParser().Parse(
            saz,
            passwordProvider: new SequencePasswordProvider(password));

        Assert.Single(report.Sessions);
    }

    [Fact]
    public void ClearsMutableProviderBufferAfterUse()
    {
        using var saz = EncryptedFixture(
            TestPassword,
            EncryptionKind.Aes256,
            HttpEntries());
        var password = TestPassword.ToCharArray();
        var provider = new CapturingPasswordProvider(password);

        Assert.Single(new SazParser().Parse(saz, passwordProvider: provider).Sessions);

        Assert.All(password, character => Assert.Equal('\0', character));
    }

    [Fact]
    public void RejectsOverlongPasswordBeforeArchiveRead()
    {
        using var saz = EncryptedFixture(
            TestPassword,
            EncryptionKind.Aes256,
            HttpEntries());

        var exception = Assert.Throws<SazPasswordTooLongException>(
            () => new SazParser().Parse(
                saz,
                passwordProvider: new SequencePasswordProvider(
                    new string('x', SazPasswordLimits.MaximumCharacters + 1))));

        Assert.Contains("1,024-character", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsCorruptAesAuthenticationCodeWithoutRetry()
    {
        using var valid = EncryptedFixture(
            TestPassword,
            EncryptionKind.Aes256,
            HttpEntries());
        var bytes = valid.ToArray();
        CorruptFirstAesAuthenticationCode(bytes);
        using var corrupt = new MemoryStream(bytes, writable: false);
        var provider = new SequencePasswordProvider(TestPassword, TestPassword, TestPassword);

        Assert.Throws<SazArchiveCorruptException>(
            () => new SazParser().Parse(corrupt, passwordProvider: provider));
        Assert.Single(provider.Requests);
    }

    [Fact]
    public void RejectsZipCryptoCrcMismatchWithoutReturningPlaintext()
    {
        using var valid = EncryptedFixture(
            TestPassword,
            EncryptionKind.ZipCrypto,
            HttpEntries());
        var bytes = valid.ToArray();
        ChangeCentralCrc(bytes, "raw/1_s.txt", 0x12345678);
        using var corrupt = new MemoryStream(bytes, writable: false);

        var exception = Assert.Throws<SazArchiveCorruptException>(
            () => new SazParser().Parse(
                corrupt,
                passwordProvider: new SequencePasswordProvider(TestPassword)));

        Assert.Contains("CRC-32", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WrapsCorruptZipCryptoCompressedData()
    {
        using var valid = EncryptedFixture(
            TestPassword,
            EncryptionKind.ZipCrypto,
            HttpEntries());
        var bytes = valid.ToArray();
        CorruptZipCryptoCompressedData(bytes, "raw/1_s.txt");
        using var corrupt = new MemoryStream(bytes, writable: false);

        Assert.Throws<SazArchiveCorruptException>(
            () => new SazParser().Parse(
                corrupt,
                passwordProvider: new SequencePasswordProvider(TestPassword)));
    }

    [Fact]
    public void RetriesAmbiguousZipCryptoPasswordVerifierCollision()
    {
        using var valid = EncryptedFixture(
            TestPassword,
            EncryptionKind.ZipCrypto,
            ("raw/1_c.txt", Bytes("GET / HTTP/1.1\r\nHost: test\r\n\r\n")));
        var bytes = valid.ToArray();
        var collidingPassword = FindZipCryptoVerifierCollision(bytes);
        using var saz = new MemoryStream(bytes, writable: false);
        var provider = new SequencePasswordProvider(collidingPassword, TestPassword);

        var report = new SazParser().Parse(saz, passwordProvider: provider);

        Assert.Single(report.Sessions);
        Assert.Equal(2, provider.Requests.Count);
    }

    [Fact]
    public void RejectsUnsupportedAesVendorVersionBeforePrompting()
    {
        using var valid = EncryptedFixture(
            TestPassword,
            EncryptionKind.Aes256,
            HttpEntries());
        var bytes = valid.ToArray();
        ChangeAesVendorVersion(bytes, 3);
        using var unsupported = new MemoryStream(bytes, writable: false);
        var provider = new SequencePasswordProvider(TestPassword);

        var exception = Assert.Throws<SazUnsupportedEncryptionException>(
            () => new SazParser().Parse(unsupported, passwordProvider: provider));

        Assert.Contains("vendor version 3", exception.Message, StringComparison.Ordinal);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public void RejectsUnsupportedEncryptedCompressionBeforePrompting()
    {
        using var valid = EncryptedFixture(
            TestPassword,
            EncryptionKind.Aes256,
            HttpEntries());
        var bytes = valid.ToArray();
        ChangeAesCompressionMethod(bytes, 12);
        using var unsupported = new MemoryStream(bytes, writable: false);
        var provider = new SequencePasswordProvider(TestPassword);

        var exception = Assert.Throws<SazUnsupportedEncryptionException>(
            () => new SazParser().Parse(unsupported, passwordProvider: provider));

        Assert.Contains("compression method 12", exception.Message, StringComparison.Ordinal);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public void RejectsPkwareStrongEncryptionBeforePrompting()
    {
        using var valid = EncryptedFixture(
            TestPassword,
            EncryptionKind.ZipCrypto,
            HttpEntries());
        var bytes = valid.ToArray();
        SetStrongEncryptionFlags(bytes);
        using var unsupported = new MemoryStream(bytes, writable: false);
        var provider = new SequencePasswordProvider(TestPassword);

        var exception = Assert.Throws<SazUnsupportedEncryptionException>(
            () => new SazParser().Parse(unsupported, passwordProvider: provider));

        Assert.Contains("strong encryption", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public void RejectsTruncatedEncryptedArchiveAsUnreadable()
    {
        using var valid = EncryptedFixture(
            TestPassword,
            EncryptionKind.Aes256,
            HttpEntries());
        var bytes = valid.ToArray();
        using var truncated = new MemoryStream(bytes[..^20], writable: false);

        var exception = Assert.Throws<InvalidDataException>(
            () => new SazParser().Parse(
                truncated,
                passwordProvider: new SequencePasswordProvider(TestPassword)));

        Assert.Contains("not a readable ZIP/SAZ archive", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsDeclaredEncryptedEntryBeyondSafetyLimit()
    {
        using var valid = EncryptedFixture(
            TestPassword,
            EncryptionKind.Aes256,
            HttpEntries());
        var bytes = valid.ToArray();
        SetFirstCentralUncompressedSize(bytes, 300u * 1024 * 1024);
        using var oversized = new MemoryStream(bytes, writable: false);

        Assert.Throws<SazArchiveLimitException>(
            () => new SazParser().Parse(
                oversized,
                passwordProvider: new SequencePasswordProvider(TestPassword)));
    }

    [Fact]
    public void GeneratedHtmlDoesNotContainPassword()
    {
        using var saz = EncryptedFixture(
            TestPassword,
            EncryptionKind.Aes256,
            HttpEntries());
        var report = new SazParser().Parse(
            saz,
            passwordProvider: new SequencePasswordProvider(TestPassword));

        var html = new HtmlReportGenerator().Generate(report);

        Assert.DoesNotContain(TestPassword, html, StringComparison.Ordinal);
        Assert.DoesNotContain("9901", html, StringComparison.OrdinalIgnoreCase);
    }

    private static (string Name, byte[] Content)[] HttpEntries() =>
    [
        ("raw/1_c.txt", Bytes("GET /encrypted HTTP/1.1\r\nHost: example.test\r\n\r\n")),
        ("raw/1_s.txt", Bytes(
            "HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nencrypted response")),
        ("raw/1_m.xml", Bytes(
            "<Session><SessionTimers ClientBeginRequest=\"2024-05-01T12:00:00Z\"/></Session>"))
    ];

    internal static MemoryStream EncryptedFixture(
        string password,
        EncryptionKind encryption,
        params (string Name, byte[] Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipOutputStream(stream))
        {
            archive.IsStreamOwner = false;
            archive.UseZip64 = UseZip64.Off;
            archive.Password = password;
            foreach (var (name, content) in entries)
            {
                var entry = new ZipEntry(name)
                {
                    CompressionMethod = CompressionMethod.Deflated,
                    AESKeySize = encryption == EncryptionKind.Aes256 ? 256 : 0
                };
                archive.PutNextEntry(entry);
                archive.Write(content);
                archive.CloseEntry();
            }
            archive.Finish();
        }
        stream.Position = 0;
        return stream;
    }

    private static MemoryStream MixedFixture(string password)
    {
        var entries = HttpEntries();
        var stream = new MemoryStream();
        using (var archive = new ZipOutputStream(stream))
        {
            archive.IsStreamOwner = false;
            archive.UseZip64 = UseZip64.Off;

            archive.Password = password;
            WriteEntry(archive, entries[0], aesKeySize: 256);

            archive.Password = password;
            WriteEntry(archive, entries[1], aesKeySize: 0);

            archive.Password = null;
            WriteEntry(archive, entries[2], aesKeySize: 0);
            archive.Finish();
        }
        stream.Position = 0;
        return stream;
    }

    private static void WriteEntry(
        ZipOutputStream archive,
        (string Name, byte[] Content) value,
        int aesKeySize)
    {
        var entry = new ZipEntry(value.Name)
        {
            CompressionMethod = CompressionMethod.Deflated,
            AESKeySize = aesKeySize
        };
        archive.PutNextEntry(entry);
        archive.Write(value.Content);
        archive.CloseEntry();
    }

    private static MemoryStream PlainFixture(params (string Name, byte[] Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
                using var output = entry.Open();
                output.Write(content);
            }
        }
        stream.Position = 0;
        return stream;
    }

    private static void CorruptFirstAesAuthenticationCode(byte[] archive)
    {
        var central = FirstCentralDirectoryEntry(archive);
        var compressedSize = ReadUInt32(archive, central + 20);
        var localOffset = checked((int)ReadUInt32(archive, central + 42));
        var nameLength = ReadUInt16(archive, localOffset + 26);
        var extraLength = ReadUInt16(archive, localOffset + 28);
        var dataOffset = localOffset + 30 + nameLength + extraLength;
        archive[checked(dataOffset + (int)compressedSize - 1)] ^= 0x5A;
    }

    private static void CorruptZipCryptoCompressedData(byte[] archive, string name)
    {
        var central = FindCentralDirectoryEntry(archive, name);
        var localOffset = checked((int)ReadUInt32(archive, central + 42));
        var nameLength = ReadUInt16(archive, localOffset + 26);
        var extraLength = ReadUInt16(archive, localOffset + 28);
        var dataOffset = localOffset + 30 + nameLength + extraLength;
        archive[dataOffset + 12] ^= 0xFF;
    }

    private static string FindZipCryptoVerifierCollision(byte[] archive)
    {
        for (var index = 0; index < 10_000; index++)
        {
            var candidate = $"wrong-{index}";
            using var stream = new MemoryStream(archive, writable: false);
            try
            {
                new SazParser().Parse(
                    stream,
                    passwordProvider: new SequencePasswordProvider(candidate));
            }
            catch (SazAuthenticationException)
            {
                continue;
            }
            catch (SazArchiveCorruptException)
            {
                return candidate;
            }
        }
        throw new InvalidOperationException("Could not find a ZipCrypto verifier collision.");
    }

    private static void ChangeAesVendorVersion(byte[] archive, ushort version)
    {
        var offset = 0;
        var changed = 0;
        while ((offset = FindExtraField(archive, offset, 0x9901)) >= 0)
        {
            WriteUInt16(archive, offset + 4, version);
            changed++;
            offset += 11;
        }
        Assert.True(changed >= 2);
    }

    private static void ChangeAesCompressionMethod(byte[] archive, ushort method)
    {
        var offset = 0;
        var changed = 0;
        while ((offset = FindExtraField(archive, offset, 0x9901)) >= 0)
        {
            WriteUInt16(archive, offset + 9, method);
            changed++;
            offset += 11;
        }
        Assert.True(changed >= 2);
    }

    private static int FindExtraField(byte[] archive, int start, ushort fieldId)
    {
        for (var index = start; index <= archive.Length - 11; index++)
        {
            if (ReadUInt16(archive, index) == fieldId
                && ReadUInt16(archive, index + 2) >= 7
                && archive[index + 6] == (byte)'A'
                && archive[index + 7] == (byte)'E')
            {
                return index;
            }
        }
        return -1;
    }

    private static void SetFirstCentralUncompressedSize(byte[] archive, uint size)
    {
        var central = FirstCentralDirectoryEntry(archive);
        WriteUInt32(archive, central + 24, size);
    }

    private static void ChangeCentralCrc(byte[] archive, string name, uint crc)
    {
        WriteUInt32(archive, FindCentralDirectoryEntry(archive, name) + 16, crc);
    }

    private static int FindCentralDirectoryEntry(byte[] archive, string name)
    {
        var end = FindLastSignature(archive, 0x06054b50);
        var offset = checked((int)ReadUInt32(archive, end + 16));
        while (offset + 46 <= end && ReadUInt32(archive, offset) == 0x02014b50)
        {
            var nameLength = ReadUInt16(archive, offset + 28);
            var extraLength = ReadUInt16(archive, offset + 30);
            var commentLength = ReadUInt16(archive, offset + 32);
            var entryName = Encoding.UTF8.GetString(archive, offset + 46, nameLength);
            if (entryName == name)
            {
                return offset;
            }
            offset += 46 + nameLength + extraLength + commentLength;
        }
        throw new InvalidOperationException("ZIP entry was not found.");
    }

    private static void SetStrongEncryptionFlags(byte[] archive)
    {
        var central = FirstCentralDirectoryEntry(archive);
        var local = checked((int)ReadUInt32(archive, central + 42));
        WriteUInt16(
            archive,
            central + 8,
            (ushort)(ReadUInt16(archive, central + 8) | (ushort)GeneralBitFlags.StrongEncryption));
        WriteUInt16(
            archive,
            local + 6,
            (ushort)(ReadUInt16(archive, local + 6) | (ushort)GeneralBitFlags.StrongEncryption));
    }

    private static int FirstCentralDirectoryEntry(byte[] archive)
    {
        var end = FindLastSignature(archive, 0x06054b50);
        var central = checked((int)ReadUInt32(archive, end + 16));
        Assert.Equal(0x02014b50u, ReadUInt32(archive, central));
        return central;
    }

    private static int FindSignature(byte[] bytes, uint signature)
    {
        for (var index = 0; index <= bytes.Length - 4; index++)
        {
            if (ReadUInt32(bytes, index) == signature)
            {
                return index;
            }
        }
        throw new InvalidOperationException("ZIP signature was not found.");
    }

    private static int FindLastSignature(byte[] bytes, uint signature)
    {
        for (var index = bytes.Length - 4; index >= 0; index--)
        {
            if (ReadUInt32(bytes, index) == signature)
            {
                return index;
            }
        }
        throw new InvalidOperationException("ZIP signature was not found.");
    }

    private static ushort ReadUInt16(byte[] bytes, int offset) =>
        (ushort)(bytes[offset] | (bytes[offset + 1] << 8));

    private static uint ReadUInt32(byte[] bytes, int offset) =>
        (uint)(bytes[offset]
            | (bytes[offset + 1] << 8)
            | (bytes[offset + 2] << 16)
            | (bytes[offset + 3] << 24));

    private static void WriteUInt16(byte[] bytes, int offset, ushort value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteUInt32(byte[] bytes, int offset, uint value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
        bytes[offset + 2] = (byte)(value >> 16);
        bytes[offset + 3] = (byte)(value >> 24);
    }

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    internal enum EncryptionKind
    {
        Aes256,
        ZipCrypto
    }

    private sealed class SequencePasswordProvider(params string[] passwords) : ISazPasswordProvider
    {
        private readonly Queue<string> passwords = new(passwords);
        private readonly int maximumAttempts = Math.Clamp(passwords.Length, 1, 3);
        public List<SazPasswordRequest> Requests { get; } = [];
        public int MaximumAttempts => maximumAttempts;

        public char[]? GetPassword(SazPasswordRequest request)
        {
            Requests.Add(request);
            return passwords.Count == 0 ? null : passwords.Dequeue().ToCharArray();
        }
    }

    private sealed class CapturingPasswordProvider(char[] password) : ISazPasswordProvider
    {
        public int MaximumAttempts => 1;
        public char[] GetPassword(SazPasswordRequest request) => password;
    }
}

public sealed class EncryptedSazCliTests
{
    private const string TestPassword = "cli neutral password";

    [Fact]
    public void InteractivePromptRetriesWithoutEchoingPassword()
    {
        using var directory = new TemporaryDirectory();
        var input = directory.PathFor("capture.saz");
        var output = directory.PathFor("report.html");
        using (var archive = EncryptedSazTests.EncryptedFixture(
            TestPassword,
            EncryptedSazTests.EncryptionKind.Aes256,
            ("raw/1_c.txt", Encoding.UTF8.GetBytes("GET / HTTP/1.1\r\nHost: test\r\n\r\n"))))
        {
            File.WriteAllBytes(input, archive.ToArray());
        }
        var console = FakeConsole.Interactive("wrong", "also wrong", TestPassword);

        var exitCode = new CliApplication(console).Run([input, output]);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(output));
        Assert.Equal(3, Count(console.Output, "Password: "));
        Assert.Contains("Incorrect password. Try again.", console.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(TestPassword, console.Output + console.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void PasswordStdinReadsOneLineAndPreservesSpaces()
    {
        const string password = "  meaningful spaces  ";
        using var directory = new TemporaryDirectory();
        var input = directory.PathFor("capture.saz");
        var output = directory.PathFor("report.html");
        using (var archive = EncryptedSazTests.EncryptedFixture(
            password,
            EncryptedSazTests.EncryptionKind.Aes256,
            ("raw/1_c.txt", Encoding.UTF8.GetBytes("GET / HTTP/1.1\r\nHost: test\r\n\r\n"))))
        {
            File.WriteAllBytes(input, archive.ToArray());
        }
        var console = FakeConsole.Redirected(password + "\r\nunused");

        var exitCode = new CliApplication(console).Run(
            ["--password-stdin", input, output]);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(output));
        Assert.DoesNotContain("Password:", console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(password, console.Output + console.Error, StringComparison.Ordinal);
        Assert.Equal("unused", console.RemainingInput);
    }

    [Fact]
    public void PasswordStdinDoesNotEchoWhenAttachedToTerminal()
    {
        using var directory = new TemporaryDirectory();
        var input = directory.PathFor("capture.saz");
        var output = directory.PathFor("report.html");
        using (var archive = EncryptedSazTests.EncryptedFixture(
            TestPassword,
            EncryptedSazTests.EncryptionKind.Aes256,
            ("raw/1_c.txt", Encoding.UTF8.GetBytes("GET / HTTP/1.1\r\nHost: test\r\n\r\n"))))
        {
            File.WriteAllBytes(input, archive.ToArray());
        }
        var console = FakeConsole.Interactive(TestPassword);

        var exitCode = new CliApplication(console).Run(
            ["--password-stdin", input, output]);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("Password:", console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(TestPassword, console.Output + console.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void PasswordStdinHasSingleAttemptAndHandlesEof()
    {
        using var directory = new TemporaryDirectory();
        var input = directory.PathFor("capture.saz");
        var output = directory.PathFor("report.html");
        using (var archive = EncryptedSazTests.EncryptedFixture(
            TestPassword,
            EncryptedSazTests.EncryptionKind.Aes256,
            ("raw/1_c.txt", Encoding.UTF8.GetBytes("GET / HTTP/1.1\r\nHost: test\r\n\r\n"))))
        {
            File.WriteAllBytes(input, archive.ToArray());
        }

        var wrong = FakeConsole.Redirected("wrong\n" + TestPassword + "\n");
        Assert.Equal(5, new CliApplication(wrong).Run(["--password-stdin", input, output]));
        Assert.Contains("password is incorrect", wrong.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(TestPassword + "\n", wrong.RemainingInput);

        var eof = FakeConsole.Redirected("");
        Assert.Equal(5, new CliApplication(eof).Run(["--password-stdin", input, output]));
        Assert.Contains("end of input", eof.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InteractiveEscapeCancelsWithoutRetry()
    {
        using var directory = new TemporaryDirectory();
        var input = directory.PathFor("capture.saz");
        using (var archive = EncryptedSazTests.EncryptedFixture(
            TestPassword,
            EncryptedSazTests.EncryptionKind.Aes256,
            ("raw/1_c.txt", Encoding.UTF8.GetBytes("GET / HTTP/1.1\r\nHost: test\r\n\r\n"))))
        {
            File.WriteAllBytes(input, archive.ToArray());
        }
        var console = FakeConsole.WithKeys(
            new ConsoleKeyInfo(
                '\u001b',
                ConsoleKey.Escape,
                shift: false,
                alt: false,
                control: false));

        Assert.Equal(5, new CliApplication(console).Run([input]));
        Assert.Contains("cancelled", console.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, Count(console.Output, "Password: "));
    }

    [Fact]
    public void RedirectedInputRequiresExplicitPasswordStdinOnlyWhenEncrypted()
    {
        using var directory = new TemporaryDirectory();
        var encryptedPath = directory.PathFor("encrypted.saz");
        var plainPath = directory.PathFor("plain.saz");
        using (var archive = EncryptedSazTests.EncryptedFixture(
            TestPassword,
            EncryptedSazTests.EncryptionKind.Aes256,
            ("raw/1_c.txt", Encoding.UTF8.GetBytes("GET / HTTP/1.1\r\nHost: test\r\n\r\n"))))
        {
            File.WriteAllBytes(encryptedPath, archive.ToArray());
        }
        using (var plain = new MemoryStream())
        {
            using (var archive = new ZipArchive(plain, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry("raw/1_c.txt");
                using var output = entry.Open();
                output.Write(Encoding.UTF8.GetBytes("GET / HTTP/1.1\r\nHost: test\r\n\r\n"));
            }
            File.WriteAllBytes(plainPath, plain.ToArray());
        }

        var encryptedConsole = FakeConsole.Redirected("");
        Assert.Equal(
            5,
            new CliApplication(encryptedConsole).Run(
                [encryptedPath, directory.PathFor("encrypted.html")]));
        Assert.Contains("--password-stdin", encryptedConsole.Error, StringComparison.Ordinal);

        var plainConsole = FakeConsole.Redirected("");
        Assert.Equal(
            0,
            new CliApplication(plainConsole).Run(
                [plainPath, directory.PathFor("plain.html")]));
        Assert.DoesNotContain("Password:", plainConsole.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsOverlongStdinAndPasswordArgument()
    {
        using var directory = new TemporaryDirectory();
        var input = directory.PathFor("capture.saz");
        using (var archive = EncryptedSazTests.EncryptedFixture(
            TestPassword,
            EncryptedSazTests.EncryptionKind.Aes256,
            ("raw/1_c.txt", Encoding.UTF8.GetBytes("GET / HTTP/1.1\r\nHost: test\r\n\r\n"))))
        {
            File.WriteAllBytes(input, archive.ToArray());
        }

        var longInput = FakeConsole.Redirected(
            new string('x', SazPasswordLimits.MaximumCharacters + 1) + "\n");
        Assert.Equal(
            5,
            new CliApplication(longInput).Run(["--password-stdin", input]));
        Assert.Contains("1,024-character", longInput.Error, StringComparison.Ordinal);

        var optionConsole = FakeConsole.Redirected("");
        Assert.Equal(
            2,
            new CliApplication(optionConsole).Run(["--password", TestPassword, input]));
        Assert.Contains("unknown option '--password'", optionConsole.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(TestPassword, optionConsole.Output + optionConsole.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void HelpDocumentsSecurePasswordBehavior()
    {
        var console = FakeConsole.Redirected("");

        Assert.Equal(0, new CliApplication(console).Run(["--help"]));

        Assert.Contains("--password-stdin", console.Output, StringComparison.Ordinal);
        Assert.Contains("up to three attempts", console.Output, StringComparison.Ordinal);
        Assert.Contains("Leading and trailing", console.Output, StringComparison.Ordinal);
        Assert.Contains("not supported", console.Output, StringComparison.Ordinal);
        Assert.Contains("--scrub-auth", console.Output, StringComparison.Ordinal);
        Assert.Contains("-ScrubAuth", console.Output, StringComparison.Ordinal);
        Assert.Contains("typed markers", console.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--scrub-auth")]
    [InlineData("-ScrubAuth")]
    public void ScrubAuthOptionRemovesSecretsAndPrintsCounts(string option)
    {
        const string canary = "cli-auth-canary-941d53e2";
        using var directory = new TemporaryDirectory();
        var input = directory.PathFor("capture.saz");
        var output = directory.PathFor("report.html");
        WritePlainSaz(
            input,
            $"POST /login?access_token={canary} HTTP/1.1\r\n"
            + "Host: example.test\r\n"
            + $"Authorization: Bearer {canary}\r\n"
            + $"Cookie: auth={canary}\r\n"
            + "Content-Type: application/json\r\n\r\n"
            + $$"""{"client_secret":"{{canary}}"}""",
            $"HTTP/1.1 200 OK\r\nSet-Cookie: session={canary}; Path=/\r\nContent-Type: text/plain\r\n\r\npassword={canary}");
        var console = FakeConsole.Redirected("");

        var exitCode = new CliApplication(console).Run([option, input, output]);

        Assert.Equal(0, exitCode);
        Assert.Contains("Authentication scrub:", console.Output, StringComparison.Ordinal);
        Assert.Contains("Bearer:", console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(canary, console.Output + console.Error, StringComparison.Ordinal);
        var html = File.ReadAllText(output);
        Assert.Contains("This report was generated with --scrub-auth.", html, StringComparison.Ordinal);
        Assert.DoesNotContain(canary, html, StringComparison.Ordinal);
        foreach (Match match in Regex.Matches(html, "data-compressed-payload=\"([A-Za-z0-9+/=]+)\""))
        {
            using var compressed = new MemoryStream(Convert.FromBase64String(match.Groups[1].Value));
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            Assert.DoesNotContain(canary, reader.ReadToEnd(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WithoutScrubAuthCliOutputMatchesDirectGeneration()
    {
        using var directory = new TemporaryDirectory();
        var input = directory.PathFor("capture.saz");
        var output = directory.PathFor("report.html");
        WritePlainSaz(
            input,
            "GET /?token=unchanged-canary HTTP/1.1\r\nHost: example.test\r\n\r\n",
            "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\n\r\nunchanged");
        var expected = new HtmlReportGenerator().Generate(new SazParser().Parse(input));

        Assert.Equal(0, new CliApplication(FakeConsole.Redirected("")).Run([input, output]));

        Assert.Equal(expected, File.ReadAllText(output));
    }

    [Fact]
    public void RejectsDuplicateScrubAuthOption()
    {
        var console = FakeConsole.Redirected("");

        Assert.Equal(2, new CliApplication(console).Run(["--scrub-auth", "-ScrubAuth", "capture.saz"]));
        Assert.Contains("may be specified only once", console.Error, StringComparison.Ordinal);
    }

    private static void WritePlainSaz(string path, string request, string response)
    {
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        WriteEntry(archive, "raw/1_c.txt", request);
        WriteEntry(archive, "raw/1_s.txt", response);
    }

    private static void WriteEntry(ZipArchive archive, string name, string value)
    {
        var entry = archive.CreateEntry(name);
        using var output = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        output.Write(value);
    }

    private static int Count(string value, string fragment)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(fragment, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += fragment.Length;
        }
        return count;
    }

    private sealed class FakeConsole : ICliConsole
    {
        private readonly Queue<ConsoleKeyInfo> keys;
        private readonly StringReader input;
        private readonly StringBuilder output = new();
        private readonly StringBuilder error = new();

        private FakeConsole(bool redirected, string standardInput, IEnumerable<ConsoleKeyInfo>? keys)
        {
            IsInputRedirected = redirected;
            input = new StringReader(standardInput);
            this.keys = new Queue<ConsoleKeyInfo>(keys ?? []);
        }

        public bool IsInputRedirected { get; }
        public TextReader In => input;
        public string Output => output.ToString();
        public string Error => error.ToString();
        public string RemainingInput => input.ReadToEnd();

        public static FakeConsole Redirected(string input) => new(true, input, null);

        public static FakeConsole WithKeys(params ConsoleKeyInfo[] keys) =>
            new(false, "", keys);

        public static FakeConsole Interactive(params string[] attempts)
        {
            var keys = attempts.SelectMany(
                attempt => attempt.Select(
                        character => new ConsoleKeyInfo(
                            character,
                            ConsoleKey.A,
                            shift: false,
                            alt: false,
                            control: false))
                    .Append(new ConsoleKeyInfo(
                        '\r',
                        ConsoleKey.Enter,
                        shift: false,
                        alt: false,
                        control: false)));
            return new FakeConsole(false, "", keys);
        }

        public void Write(string value) => output.Append(value);
        public void WriteLine(string value = "") => output.AppendLine(value);
        public void WriteErrorLine(string value) => error.AppendLine(value);
        public ConsoleKeyInfo ReadKey(bool intercept) => keys.Dequeue();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"saz-viewer-encrypted-tests-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(path);
        public string PathFor(string name) => System.IO.Path.Combine(path, name);

        public void Dispose()
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }
}
