using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.IO;
using StatusBar.Core.Projects;

namespace ClaudeUsageWidget;

internal enum ProjectOverviewLoadSource
{
    Empty,
    Primary,
    Recovery,
    Unavailable,
}

internal sealed record ProjectOverviewLoadResult(
    ProjectOverviewDocument Document,
    ProjectOverviewLoadSource Source,
    string? DiagnosticCode);

/// <summary>Stores derived project state encrypted for the current Windows user.</summary>
internal sealed class ProjectOverviewRepository
{
    const int MaximumEncryptedBytes = 8 * 1024 * 1024;
    const int MaximumPlaintextBytes = 4 * 1024 * 1024;
    static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 32 };
    readonly string _primaryPath;
    readonly string _recoveryPath;
    readonly string _temporaryPath;
    readonly string _primaryQuarantinePath;
    readonly string _recoveryQuarantinePath;

    internal ProjectOverviewRepository(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _primaryPath = Path.Combine(dataDirectory, "projects.dat");
        _recoveryPath = Path.Combine(dataDirectory, "projects.recovery.dat");
        _temporaryPath = Path.Combine(dataDirectory, "projects.pending.dat");
        _primaryQuarantinePath = Path.Combine(dataDirectory, "projects.unreadable.dat");
        _recoveryQuarantinePath = Path.Combine(dataDirectory, "projects-recovery.unreadable.dat");
    }

    internal ProjectOverviewLoadResult Load()
    {
        var primary = TryRead(_primaryPath);
        if (primary.Document is not null)
            return new(primary.Document, ProjectOverviewLoadSource.Primary, null);

        var recovery = TryRead(_recoveryPath);
        if (recovery.Document is not null)
            return new(recovery.Document, ProjectOverviewLoadSource.Recovery, primary.ErrorCode ?? "PrimaryUnavailable");

        if (!primary.Exists && !recovery.Exists && !File.Exists(_primaryQuarantinePath) && !File.Exists(_recoveryQuarantinePath))
            return new(ProjectOverviewDocumentRules.Clear(), ProjectOverviewLoadSource.Empty, null);

        // Keep encrypted files for later recovery; the app can rebuild missing summaries within its budget.
        return new(ProjectOverviewDocumentRules.Clear(), ProjectOverviewLoadSource.Unavailable,
            primary.ErrorCode ?? recovery.ErrorCode ?? "StoredDataUnavailable");
    }

    internal void Save(ProjectOverviewDocument document) => SaveCore(document, purgeRecovery: false);

    internal void DeleteProject(ProjectOverviewDocument document, string projectId) =>
        SaveCore(ProjectOverviewDocumentRules.DeleteProject(document, projectId), purgeRecovery: true);

    internal void Clear()
    {
        DeleteIfPresent(_temporaryPath);
        DeleteIfPresent(_primaryPath);
        DeleteIfPresent(_recoveryPath);
        DeleteIfPresent(_primaryQuarantinePath);
        DeleteIfPresent(_recoveryQuarantinePath);
    }

    void SaveCore(ProjectOverviewDocument document, bool purgeRecovery)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!ProjectOverviewDocumentValidator.TryValidate(document, out var validationError))
            throw new InvalidDataException(validationError);

        // Never replace state that could not be decrypted or parsed: it may contain manual
        // mappings that become recoverable after an app update or user-profile repair.
        var primary = TryRead(_primaryPath);
        var recovery = TryRead(_recoveryPath);
        if ((primary.Exists && primary.Document is null) || (recovery.Exists && recovery.Document is null))
        {
            if (recovery.Document is null && primary.Document is null)
                throw new InvalidDataException("StoredDataUnavailable");
            if (purgeRecovery && primary.Document is null)
                throw new InvalidDataException("StoredDataUnavailable");
        }

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (plaintext.Length > MaximumPlaintextBytes)
            throw new InvalidDataException("ProjectOverviewTooLarge");
        var protectedBytes = CurrentUserProtection.Protect(plaintext);
        if (protectedBytes.Length > MaximumEncryptedBytes)
            throw new InvalidDataException("ProjectOverviewTooLarge");

        var directory = Path.GetDirectoryName(_primaryPath)!;
        Directory.CreateDirectory(directory);
        try
        {
            using (var stream = new FileStream(_temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None,
                       64 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(protectedBytes);
                stream.Flush(flushToDisk: true);
            }

            if (primary.Exists && primary.Document is null)
                Quarantine(_primaryPath, _primaryQuarantinePath);
            if (!purgeRecovery && recovery.Exists && recovery.Document is null)
                Quarantine(_recoveryPath, _recoveryQuarantinePath);

            var primaryIsReadable = primary.Document is not null;
            if (File.Exists(_primaryPath) && primaryIsReadable)
            {
                File.Replace(_temporaryPath, _primaryPath, purgeRecovery ? null : _recoveryPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(_temporaryPath, _primaryPath, overwrite: true);
            }

            if (purgeRecovery) DeleteIfPresent(_recoveryPath);
        }
        catch
        {
            DeleteIfPresent(_temporaryPath);
            throw;
        }
    }

    (ProjectOverviewDocument? Document, bool Exists, string? ErrorCode) TryRead(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return (null, false, null);
            if (info.Length is <= 0 or > MaximumEncryptedBytes) return (null, true, "StoredDataSizeInvalid");
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length is <= 0 or > MaximumEncryptedBytes) return (null, true, "StoredDataSizeInvalid");
            var plaintext = CurrentUserProtection.Unprotect(bytes);
            if (plaintext.Length > MaximumPlaintextBytes) return (null, true, "StoredDataSizeInvalid");
            var document = JsonSerializer.Deserialize<ProjectOverviewDocument>(plaintext, JsonOptions);
            if (!ProjectOverviewDocumentValidator.TryValidate(document, out var validationError))
                return (null, true, validationError);
            return (document, true, null);
        }
        catch (CryptographicException)
        {
            return (null, true, "StoredDataProtectionFailed");
        }
        catch (JsonException)
        {
            return (null, true, "StoredDataMalformed");
        }
        catch (IOException)
        {
            return (null, true, "StoredDataUnavailable");
        }
        catch (UnauthorizedAccessException)
        {
            return (null, true, "StoredDataUnavailable");
        }
    }

    static void DeleteIfPresent(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    static void Quarantine(string source, string destination)
    {
        if (File.Exists(destination)) throw new InvalidDataException("StoredDataQuarantineOccupied");
        File.Move(source, destination);
    }

    static class CurrentUserProtection
    {
        const uint UiForbidden = 0x1;

        internal static byte[] Protect(byte[] input) => Transform(input, protect: true);
        internal static byte[] Unprotect(byte[] input) => Transform(input, protect: false);

        static byte[] Transform(byte[] input, bool protect)
        {
            var inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
            try
            {
                var inputBlob = new DataBlob(input.Length, inputHandle.AddrOfPinnedObject());
                DataBlob outputBlob;
                var succeeded = protect
                    ? CryptProtectData(ref inputBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out outputBlob)
                    : CryptUnprotectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out outputBlob);
                if (!succeeded) throw new CryptographicException(Marshal.GetLastWin32Error());
                try
                {
                    var output = new byte[outputBlob.Length];
                    Marshal.Copy(outputBlob.Data, output, 0, output.Length);
                    return output;
                }
                finally
                {
                    LocalFree(outputBlob.Data);
                }
            }
            finally
            {
                inputHandle.Free();
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct DataBlob(int length, IntPtr data)
        {
            public int Length = length;
            public IntPtr Data = data;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CryptProtectData(
            ref DataBlob dataIn,
            string? description,
            IntPtr optionalEntropy,
            IntPtr reserved,
            IntPtr prompt,
            uint flags,
            out DataBlob dataOut);

        [DllImport("crypt32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CryptUnprotectData(
            ref DataBlob dataIn,
            IntPtr description,
            IntPtr optionalEntropy,
            IntPtr reserved,
            IntPtr prompt,
            uint flags,
            out DataBlob dataOut);

        [DllImport("kernel32.dll")]
        static extern IntPtr LocalFree(IntPtr memory);
    }
}
