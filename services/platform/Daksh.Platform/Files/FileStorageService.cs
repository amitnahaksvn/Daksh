using System.Security.Cryptography;
using Daksh.Platform.Data;
using Daksh.Platform.Domain;
using Daksh.Platform.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Daksh.Platform.Files;

/// <summary>
/// Stores uploaded and generated files on local disk under a per-workspace directory, named by content hash so
/// identical uploads are deduplicated. Never trusts the client-supplied name for the path (MD-007).
/// </summary>
public sealed class FileStorageService(PlatformDbContext db, IOptions<StorageOptions> options, IClock clock)
{
    private string Root(Guid workspaceId) => Path.Combine(options.Value.Root, "files", workspaceId.ToString("N"));

    public async Task<StoredFile> SaveAsync(Guid workspaceId, string name, string contentType, byte[] content, string purpose,
        Guid? ownerUserId, Guid? agentId, CancellationToken ct)
    {
        if (content.LongLength > options.Value.MaxFileSizeBytes)
            throw new InvalidOperationException($"File exceeds the maximum size of {options.Value.MaxFileSizeBytes} bytes.");
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        var root = Root(workspaceId);
        Directory.CreateDirectory(root);
        var relative = $"{hash[..2]}/{hash}";
        var full = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        if (!File.Exists(full)) await File.WriteAllBytesAsync(full, content, ct);

        var file = new StoredFile
        {
            WorkspaceId = workspaceId, OwnerUserId = ownerUserId, AgentId = agentId, Name = SafeName(name),
            ContentType = contentType, Size = content.LongLength, Sha256 = hash, StoragePath = relative, Purpose = purpose,
            ScanStatus = "not_scanned",
        };
        db.Files.Add(file);
        await db.SaveChangesAsync(ct);
        return file;
    }

    public async Task<byte[]> ReadAsync(StoredFile file, CancellationToken ct)
    {
        var full = Path.Combine(Root(file.WorkspaceId), file.StoragePath);
        if (!File.Exists(full)) throw new FileNotFoundException("Stored file is missing from disk.", full);
        return await File.ReadAllBytesAsync(full, ct);
    }

    public async Task PurgeExpiredAsync(Guid workspaceId, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var expired = db.Files.Where(f => f.WorkspaceId == workspaceId && f.ExpiresAt != null && f.ExpiresAt < now);
        await foreach (var f in expired.AsAsyncEnumerable().WithCancellation(ct)) f.DeletedAt = now;
        await db.SaveChangesAsync(ct);
    }

    private static string SafeName(string name)
    {
        var fileName = Path.GetFileName(name.Replace('\\', '/'));
        return string.IsNullOrWhiteSpace(fileName) ? "file" : fileName.Length > 200 ? fileName[..200] : fileName;
    }
}
