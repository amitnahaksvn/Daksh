using Daksh.Platform.Domain;
using Daksh.Platform.Infrastructure;
using Microsoft.Extensions.Options;

namespace Daksh.Platform.Files;

/// <summary>
/// Stores uploaded and generated files on local disk under a per-workspace directory, named by content hash so
/// identical uploads are deduplicated. Never trusts the client-supplied name for the path (MD-007).
/// File metadata persistence (<see cref="SaveAsync"/>, <see cref="PurgeExpiredAsync"/>) is pending MongoDB
/// rebuild — previously recorded via PlatformDbContext.Files. <see cref="ReadAsync"/> only reads from disk
/// given an already-resolved <see cref="StoredFile"/>, so it keeps working.
/// </summary>
public sealed class FileStorageService(IOptions<StorageOptions> options)
{
    private string Root(Guid workspaceId) => Path.Combine(options.Value.Root, "files", workspaceId.ToString("N"));

    public Task<StoredFile> SaveAsync(Guid workspaceId, string name, string contentType, byte[] content, string purpose,
        Guid? ownerUserId, Guid? agentId, CancellationToken ct) =>
        throw new NotImplementedException("File metadata storage is unavailable: pending MongoDB rebuild.");

    public async Task<byte[]> ReadAsync(StoredFile file, CancellationToken ct)
    {
        var full = Path.Combine(Root(file.WorkspaceId), file.StoragePath);
        if (!File.Exists(full)) throw new FileNotFoundException("Stored file is missing from disk.", full);
        return await File.ReadAllBytesAsync(full, ct);
    }

    public Task PurgeExpiredAsync(Guid workspaceId, CancellationToken ct) =>
        throw new NotImplementedException("File metadata storage is unavailable: pending MongoDB rebuild.");

    private static string SafeName(string name)
    {
        var fileName = Path.GetFileName(name.Replace('\\', '/'));
        return string.IsNullOrWhiteSpace(fileName) ? "file" : fileName.Length > 200 ? fileName[..200] : fileName;
    }
}
