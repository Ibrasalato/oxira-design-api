using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Oxira.Design.Application.Common;

namespace Oxira.Design.Infrastructure.Services;

/// <summary>"Storage" section. Root: folder for order files (relative paths are under the app's content root). On IIS give the app pool write access.</summary>
public class StorageOptions
{
    public string Root { get; set; } = "App_Data/files";
}

public class DiskFileStore(IOptions<StorageOptions> opt, IHostEnvironment env) : IFileStore
{
    private string Root => Path.GetFullPath(Path.IsPathRooted(opt.Value.Root) ? opt.Value.Root : Path.Combine(env.ContentRootPath, opt.Value.Root));

    private string Full(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(Root, relative));
        if (!full.StartsWith(Root, StringComparison.Ordinal)) throw new InvalidOperationException("path outside the storage root");
        return full;
    }

    public async Task<string> SaveAsync(string folder, string name, byte[] content, CancellationToken ct = default)
    {
        // a short random prefix keeps two files with the same name apart
        var relative = Path.Combine(folder, Guid.NewGuid().ToString("N")[..8] + "-" + Path.GetFileName(name)).Replace('\\', '/');
        var full = Full(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, content, ct);
        return relative;
    }

    public Task<byte[]> ReadAsync(string path, CancellationToken ct = default) => File.ReadAllBytesAsync(Full(path), ct);

    public Stream OpenRead(string path) => File.OpenRead(Full(path));
}
