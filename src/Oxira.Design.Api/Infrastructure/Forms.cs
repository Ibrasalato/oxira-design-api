using Oxira.Design.Application.Features.Orders;
using Oxira.Design.Domain;

namespace Oxira.Design.Api.Infrastructure;

/// <summary>Multipart forms (order form, designer delivery) → plain fields and files for the application layer.</summary>
public static class Forms
{
    public static async Task<(Dictionary<string, string> Fields, List<UploadedFile> Files)> ReadAsync(HttpRequest req, long maxFileBytes, CancellationToken ct)
    {
        if (!req.HasFormContentType) throw DomainException.Invalid("أرسل النموذج كـ multipart/form-data");
        var form = await req.ReadFormAsync(ct);
        var fields = form.ToDictionary(kv => kv.Key, kv => kv.Value.ToString(), StringComparer.Ordinal);
        if (form.Files.Sum(f => f.Length) > maxFileBytes) throw DomainException.TooBig("حجم الملفات كبير جدًا");
        var files = new List<UploadedFile>();
        foreach (var f in form.Files)
        {
            using var ms = new MemoryStream((int)f.Length);
            await f.CopyToAsync(ms, ct);
            files.Add(new UploadedFile(f.Name, f.FileName, f.ContentType ?? "application/octet-stream", ms.ToArray()));
        }
        return (fields, files);
    }
}
