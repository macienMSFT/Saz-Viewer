using System.Buffers;
using System.IO.Compression;
using ICSharpCode.SharpZipLib;
using ICSharpCode.SharpZipLib.Checksum;
using ICSharpCode.SharpZipLib.Zip;
using SharpZipFile = ICSharpCode.SharpZipLib.Zip.ZipFile;

namespace SazViewer.Core;

internal interface ISazArchive : IDisposable
{
    IReadOnlyList<ISazArchiveEntry> Entries { get; }
}

internal interface ISazArchiveEntry
{
    string FullName { get; }
    long Length { get; }
    Stream Open();
}

internal static class SazArchiveFactory
{
    private const int MaxEntries = 100_000;
    private const long MaxEncryptedEntryBytes = 256L * 1024 * 1024;
    private const long MaxEncryptedTotalBytes = 1024L * 1024 * 1024;
    private const long MaxNonSeekableArchiveBytes = 512L * 1024 * 1024;
    private const long MaxCompressionRatio = 1_000;

    public static ISazArchive Open(
        Stream input,
        bool leaveOpen,
        ISazPasswordProvider? passwordProvider)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead)
        {
            throw new ArgumentException("The archive stream must be readable.", nameof(input));
        }

        Stream source = input;
        var sourceLeaveOpen = leaveOpen;
        if (!source.CanSeek)
        {
            source = CopySeekable(source);
            sourceLeaveOpen = false;
        }

        try
        {
            return OpenSeekable(source, sourceLeaveOpen, passwordProvider);
        }
        catch
        {
            if (!ReferenceEquals(source, input))
            {
                source.Dispose();
            }
            throw;
        }
    }

    private static ISazArchive OpenSeekable(
        Stream source,
        bool leaveOpen,
        ISazPasswordProvider? passwordProvider)
    {
        var startPosition = source.Position;
        SharpZipFile inspection;
        try
        {
            inspection = new SharpZipFile(source, true, StringCodec.Default);
        }
        catch (Exception exception) when (exception is SharpZipBaseException or IOException)
        {
            throw new InvalidDataException("The file is not a readable ZIP/SAZ archive.", exception);
        }

        try
        {
            ValidateEntries(inspection);
            var encryptedEntries = Entries(inspection).Where(entry => entry.IsCrypted).ToArray();
            if (encryptedEntries.Length == 0)
            {
                ((IDisposable)inspection).Dispose();
                inspection = null!;
                source.Position = startPosition;
                return new DotNetSazArchive(source, leaveOpen);
            }

            ValidateEncryption(encryptedEntries);
            if (passwordProvider is null)
            {
                throw new SazPasswordRequiredException();
            }

            var archive = new EncryptedSazArchive(
                leaveOpen,
                inspection,
                MaxEncryptedEntryBytes,
                MaxEncryptedTotalBytes);
            inspection = null!;
            Authenticate(archive, encryptedEntries, passwordProvider);
            return archive;
        }
        catch (SharpZipBaseException exception)
        {
            throw new InvalidDataException("The file is not a readable ZIP/SAZ archive.", exception);
        }
        finally
        {
            if (inspection is not null)
            {
                ((IDisposable)inspection).Dispose();
            }
        }
    }

    private static void Authenticate(
        EncryptedSazArchive archive,
        IReadOnlyList<ZipEntry> encryptedEntries,
        ISazPasswordProvider passwordProvider)
    {
        var candidate = encryptedEntries
            .Where(entry => entry.IsFile && entry.Size >= 0)
            .OrderBy(entry => entry.AESKeySize == 0)
            .ThenBy(entry => entry.Size)
            .FirstOrDefault();
        if (candidate is null)
        {
            archive.Dispose();
            throw new SazArchiveCorruptException("no encrypted file entry could be authenticated.");
        }

        var maximumAttempts = Math.Clamp(passwordProvider.MaximumAttempts, 1, 3);
        SazArchiveCorruptException? ambiguousZipCryptoFailure = null;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            char[]? passwordBuffer = null;
            try
            {
                passwordBuffer = passwordProvider.GetPassword(
                    new SazPasswordRequest(attempt, maximumAttempts, attempt > 1));
                if (passwordBuffer is null)
                {
                    archive.Dispose();
                    throw new SazPasswordCancelledException();
                }
                if (passwordBuffer.Length > SazPasswordLimits.MaximumCharacters)
                {
                    archive.Dispose();
                    throw new SazPasswordTooLongException(SazPasswordLimits.MaximumCharacters);
                }

                archive.Password = new string(passwordBuffer);
                archive.AuthenticateAndCache(candidate);
                return;
            }
            catch (SazAuthenticationException) when (attempt < maximumAttempts)
            {
            }
            catch (SazAuthenticationException)
            {
                archive.Dispose();
                if (ambiguousZipCryptoFailure is not null)
                {
                    throw ambiguousZipCryptoFailure;
                }
                throw new SazAuthenticationException();
            }
            catch (AmbiguousZipCryptoPasswordException exception) when (attempt < maximumAttempts)
            {
                ambiguousZipCryptoFailure ??= exception.Corruption;
            }
            catch (AmbiguousZipCryptoPasswordException exception)
            {
                archive.Dispose();
                throw ambiguousZipCryptoFailure ?? exception.Corruption;
            }
            catch
            {
                archive.Dispose();
                throw;
            }
            finally
            {
                if (passwordBuffer is not null)
                {
                    Array.Clear(passwordBuffer);
                }
            }
        }

        archive.Dispose();
        throw new SazAuthenticationException();
    }

    private static void ValidateEntries(SharpZipFile archive)
    {
        if (archive.Count > MaxEntries)
        {
            throw new SazArchiveLimitException(
                $"entry count exceeds {MaxEntries:N0}.");
        }

        foreach (var entry in Entries(archive))
        {
            ValidatePath(entry.Name);
            if (entry.Size < 0 || entry.CompressedSize < 0)
            {
                throw new InvalidDataException("An archive entry has an invalid declared size.");
            }
        }
    }

    private static void ValidateEncryption(IEnumerable<ZipEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.AESKeySize == 0)
            {
                var flags = (GeneralBitFlags)entry.Flags;
                if ((flags & (GeneralBitFlags.StrongEncryption | GeneralBitFlags.HeaderMasked)) != 0)
                {
                    throw new SazUnsupportedEncryptionException("PKWARE strong encryption.");
                }
                if (entry.CompressionMethod != CompressionMethod.Deflated)
                {
                    throw new SazUnsupportedEncryptionException(
                        $"traditional ZIP encryption with compression method {(int)entry.CompressionMethod}.");
                }
                continue;
            }

            var aes = ReadAesExtra(entry.ExtraData);
            if (aes is null)
            {
                throw new SazUnsupportedEncryptionException("an AES entry is missing its AES metadata.");
            }
            if (aes.Value.Version != 2)
            {
                throw new SazUnsupportedEncryptionException($"WinZip AES vendor version {aes.Value.Version}.");
            }
            if (aes.Value.VendorId != 0x4541)
            {
                throw new SazUnsupportedEncryptionException("an unknown AES vendor.");
            }
            if (aes.Value.Strength != 3 || entry.AESKeySize != 256)
            {
                throw new SazUnsupportedEncryptionException($"AES-{entry.AESKeySize}.");
            }
            if (aes.Value.Method != (ushort)CompressionMethod.Deflated)
            {
                throw new SazUnsupportedEncryptionException(
                    $"WinZip AES with compression method {aes.Value.Method}.");
            }
        }
    }

    private static (ushort Version, ushort VendorId, byte Strength, ushort Method)? ReadAesExtra(
        byte[]? extra)
    {
        if (extra is null)
        {
            return null;
        }

        var offset = 0;
        while (offset + 4 <= extra.Length)
        {
            var header = (ushort)(extra[offset] | (extra[offset + 1] << 8));
            var length = extra[offset + 2] | (extra[offset + 3] << 8);
            offset += 4;
            if (length < 0 || offset + length > extra.Length)
            {
                return null;
            }
            if (header == 0x9901 && length >= 7)
            {
                return (
                    (ushort)(extra[offset] | (extra[offset + 1] << 8)),
                    (ushort)(extra[offset + 2] | (extra[offset + 3] << 8)),
                    extra[offset + 4],
                    (ushort)(extra[offset + 5] | (extra[offset + 6] << 8)));
            }
            offset += length;
        }
        return null;
    }

    private static IEnumerable<ZipEntry> Entries(SharpZipFile archive)
    {
        for (var index = 0; index < archive.Count; index++)
        {
            yield return archive[index];
        }
    }

    private static void ValidatePath(string name)
    {
        var normalized = name.Replace('\\', '/');
        if (normalized.Length == 0
            || normalized.IndexOf('\0') >= 0
            || normalized.StartsWith('/')
            || (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':')
            || normalized.Split('/').Any(part => part == ".."))
        {
            throw new InvalidDataException("The archive contains an unsafe entry path.");
        }
    }

    private static MemoryStream CopySeekable(Stream input)
    {
        var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                var read = input.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }
                if (output.Length > MaxNonSeekableArchiveBytes - read)
                {
                    throw new SazArchiveLimitException(
                        $"a non-seekable archive exceeds {MaxNonSeekableArchiveBytes / (1024 * 1024):N0} MiB.");
                }
                output.Write(buffer, 0, read);
            }
            output.Position = 0;
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private sealed class DotNetSazArchive : ISazArchive
    {
        private readonly ZipArchive archive;

        public DotNetSazArchive(Stream stream, bool leaveOpen)
        {
            archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen);
            Entries = archive.Entries.Select(entry => (ISazArchiveEntry)new DotNetEntry(entry)).ToArray();
        }

        public IReadOnlyList<ISazArchiveEntry> Entries { get; }

        public void Dispose() => archive.Dispose();

        private sealed class DotNetEntry(ZipArchiveEntry entry) : ISazArchiveEntry
        {
            public string FullName => entry.FullName;
            public long Length => entry.Length;
            public Stream Open() => entry.Open();
        }
    }

    private sealed class EncryptedSazArchive : ISazArchive
    {
        private readonly SharpZipFile archive;
        private readonly Dictionary<long, byte[]> authenticatedCache = [];
        private readonly long maxEntryBytes;
        private readonly long maxTotalBytes;
        private long totalBytes;
        private bool disposed;

        public EncryptedSazArchive(
            bool leaveOpen,
            SharpZipFile archive,
            long maxEntryBytes,
            long maxTotalBytes)
        {
            this.archive = archive;
            this.archive.IsStreamOwner = !leaveOpen;
            this.maxEntryBytes = maxEntryBytes;
            this.maxTotalBytes = maxTotalBytes;
            Entries = SazArchiveFactory.Entries(archive)
                .Select(entry => (ISazArchiveEntry)new EncryptedEntry(this, entry))
                .ToArray();
        }

        public IReadOnlyList<ISazArchiveEntry> Entries { get; }

        public string? Password
        {
            set => archive.Password = value;
        }

        public void AuthenticateAndCache(ZipEntry entry)
        {
            try
            {
                authenticatedCache[entry.ZipFileIndex] = ReadEntry(entry, authenticatingPassword: true);
            }
            catch (SazArchiveCorruptException exception) when (entry.AESKeySize == 0)
            {
                throw new AmbiguousZipCryptoPasswordException(exception);
            }
        }

        public Stream Open(ZipEntry entry)
        {
            if (authenticatedCache.Remove(entry.ZipFileIndex, out var cached))
            {
                return new ClearingMemoryStream(cached);
            }

            try
            {
                return new ClearingMemoryStream(ReadEntry(entry, authenticatingPassword: false));
            }
            catch (SazArchiveException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is SharpZipBaseException or InvalidDataException or IOException)
            {
                throw new SazArchiveCorruptException("an entry could not be fully authenticated and read.", exception);
            }
        }

        private byte[] ReadEntry(ZipEntry entry, bool authenticatingPassword)
        {
            if (entry.Size > maxEntryBytes)
            {
                throw new SazArchiveLimitException(
                    $"an encrypted entry exceeds {maxEntryBytes / (1024 * 1024):N0} MiB.");
            }
            if (entry.CompressedSize > 0
                && entry.Size > 1024 * 1024
                && entry.Size / entry.CompressedSize > MaxCompressionRatio)
            {
                throw new SazArchiveLimitException(
                    $"an encrypted entry exceeds the {MaxCompressionRatio:N0}:1 compression-ratio limit.");
            }

            try
            {
                using var input = archive.GetInputStream(entry);
                using var output = new MemoryStream(
                    entry.Size > 0 ? (int)Math.Min(entry.Size, 1024 * 1024) : 0);
                var crc = entry.AESKeySize == 0 ? new Crc32() : null;
                var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                try
                {
                    while (true)
                    {
                        var read = input.Read(buffer, 0, buffer.Length);
                        if (read == 0)
                        {
                            break;
                        }
                        if (output.Length > maxEntryBytes - read)
                        {
                            throw new SazArchiveLimitException(
                                $"an encrypted entry expands beyond {maxEntryBytes / (1024 * 1024):N0} MiB.");
                        }
                        output.Write(buffer, 0, read);
                        crc?.Update(new ArraySegment<byte>(buffer, 0, read));
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                }

                if (entry.Size != output.Length)
                {
                    throw new SazArchiveCorruptException(
                        "an encrypted entry's authenticated length does not match its declared length.");
                }
                if (crc is not null && (uint)crc.Value != (uint)entry.Crc)
                {
                    throw new SazArchiveCorruptException(
                        "an entry failed CRC-32 integrity validation.");
                }
                if (totalBytes > maxTotalBytes - output.Length)
                {
                    throw new SazArchiveLimitException(
                        $"decrypted data exceeds {maxTotalBytes / (1024 * 1024):N0} MiB.");
                }
                totalBytes += output.Length;
                return output.ToArray();
            }
            catch (ZipException exception) when (
                authenticatingPassword
                && exception.Message.StartsWith("Invalid password", StringComparison.OrdinalIgnoreCase))
            {
                throw new SazAuthenticationException();
            }
            catch (SazArchiveException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is SharpZipBaseException or InvalidDataException or IOException)
            {
                throw new SazArchiveCorruptException(
                    "an encrypted entry could not be fully authenticated and read.",
                    exception);
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            archive.Password = null;
            ((IDisposable)archive).Dispose();
            foreach (var bytes in authenticatedCache.Values)
            {
                Array.Clear(bytes);
            }
            authenticatedCache.Clear();
        }

        private sealed class EncryptedEntry(EncryptedSazArchive owner, ZipEntry entry)
            : ISazArchiveEntry
        {
            public string FullName => entry.Name;
            public long Length => entry.Size;
            public Stream Open() => owner.Open(entry);
        }
    }

    private sealed class ClearingMemoryStream(byte[] bytes)
        : MemoryStream(bytes, 0, bytes.Length, writable: false, publiclyVisible: false)
    {
        private byte[]? buffer = bytes;

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && buffer is not null)
            {
                Array.Clear(buffer);
                buffer = null;
            }
        }
    }

    private sealed class AmbiguousZipCryptoPasswordException(
        SazArchiveCorruptException corruption) : Exception
    {
        public SazArchiveCorruptException Corruption { get; } = corruption;
    }
}
