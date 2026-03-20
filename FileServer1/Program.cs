using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
static string SanitizeFileName(string? fileName)
{
    if (string.IsNullOrWhiteSpace(fileName)) return string.Empty;
    return Path.GetFileName(fileName);
}

static string GetUniquePath(string directory, string fileName)
{
    var safeName = SanitizeFileName(fileName);
    if (string.IsNullOrWhiteSpace(safeName))
        throw new ArgumentException("Invalid file name.", nameof(fileName));

    var fullPath = Path.Combine(directory, safeName);
    if (!File.Exists(fullPath)) return fullPath;

    var ext = Path.GetExtension(safeName);
    var nameWithoutExt = Path.GetFileNameWithoutExtension(safeName);
    var unique = $"{nameWithoutExt}_{Guid.NewGuid():N}{ext}";
    return Path.Combine(directory, unique);
}

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Keep storage relative to the app working directory (matches your initial code).
string storagePath = Path.Combine(Directory.GetCurrentDirectory(), "storage");
string trashPath = Path.Combine(storagePath, ".trash");
string metaPath = Path.Combine(storagePath, ".meta.json");
if (!Directory.Exists(storagePath))
{
    Directory.CreateDirectory(storagePath);
}
if (!Directory.Exists(trashPath))
{
    Directory.CreateDirectory(trashPath);
}

var contentTypeProvider = new FileExtensionContentTypeProvider();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/api/upload", async ([FromForm] IFormFile file) =>
{
    if (file is null || file.Length == 0)
        return Results.BadRequest("Missing file.");

    var uniquePath = GetUniquePath(storagePath, file.FileName);

    await using var stream = File.Create(uniquePath);
    await file.CopyToAsync(stream);

    var fileName = Path.GetFileName(uniquePath);

    await MetaStore.Lock.WaitAsync();
    try
    {
        var meta = await MetaStore.LoadAsync(metaPath);
        meta[fileName] = new MetaStore.FileMeta(DateTime.UtcNow.ToString("o"), Starred: false, Trashed: false, TrashedUtc: null);
        await MetaStore.SaveAsync(metaPath, meta);
    }
    finally
    {
        MetaStore.Lock.Release();
    }

    return Results.Created($"/api/download/{fileName}", new { fileName });
}).DisableAntiforgery();

// Upload multiple files (for drag-and-drop UI).
app.MapPost("/api/upload-multiple", async ([FromForm] IFormFileCollection files) =>
{
    if (files is null || files.Count == 0)
        return Results.BadRequest("Missing files.");

    var savedNames = new List<string>();

    foreach (var file in files)
    {
        if (file is null || file.Length == 0) continue;

        var uniquePath = GetUniquePath(storagePath, file.FileName);

        await using var stream = File.Create(uniquePath);
        await file.CopyToAsync(stream);

        var fileName = Path.GetFileName(uniquePath);
        savedNames.Add(fileName);
    }

    await MetaStore.Lock.WaitAsync();
    try
    {
        var meta = await MetaStore.LoadAsync(metaPath);
        var now = DateTime.UtcNow.ToString("o");
        foreach (var name in savedNames)
        {
            meta[name] = new MetaStore.FileMeta(now, Starred: false, Trashed: false, TrashedUtc: null);
        }
        await MetaStore.SaveAsync(metaPath, meta);
    }
    finally
    {
        MetaStore.Lock.Release();
    }

    return Results.Ok(savedNames.Select(fileName => new { fileName }));
}).DisableAntiforgery();

app.MapGet("/api/download/{fileName}", (string fileName) =>
{
    var safeName = SanitizeFileName(fileName);
    if (string.IsNullOrWhiteSpace(safeName))
        return Results.BadRequest("Invalid file name.");

    var path = Path.Combine(storagePath, safeName);
    if (!File.Exists(path))
        return Results.NotFound();

    return Results.File(path, "application/octet-stream", safeName);
});

// ── Serve file inline with correct MIME type (for in-browser preview) ──────────
app.MapGet("/api/preview/{fileName}", (string fileName) =>
{
    var safeName = SanitizeFileName(fileName);
    if (string.IsNullOrWhiteSpace(safeName))
        return Results.BadRequest("Invalid file name.");

    // Preview only for non-trashed files.
    var path = Path.Combine(storagePath, safeName);
    if (!File.Exists(path)) return Results.NotFound();

    if (!contentTypeProvider.TryGetContentType(safeName, out var contentType))
        contentType = "application/octet-stream";

    // enableRangeProcessing lets the browser seek within audio/video streams.
    return Results.File(path, contentType, enableRangeProcessing: true);
});

// ── List files with metadata (uploaded/starred/trashed) ─────────────────────────
app.MapGet("/api/files", async (bool includeTrashed) =>
{
    if (!Directory.Exists(storagePath)) return Results.Ok(Array.Empty<object>());

    await MetaStore.Lock.WaitAsync();
    try
    {
        var meta = await MetaStore.LoadAsync(metaPath);

        // Ensure we don't treat meta or trash folder as regular files.
        var activeFiles = Directory.EnumerateFiles(storagePath)
            .Where(p =>
            {
                var name = Path.GetFileName(p);
                return !string.Equals(name, Path.GetFileName(metaPath), StringComparison.OrdinalIgnoreCase);
            })
            .Select(p =>
            {
                var info = new FileInfo(p);
                var m = MetaStore.EnsureFor(info.Name, info, meta);
                return (Dto: MetaStore.ToDto(info.Name, info, m), Meta: m);
            })
            .Where(x => includeTrashed || !x.Meta.Trashed)
            .Select(x => x.Dto)
            .ToList();

        if (includeTrashed)
        {
            var trashFiles = Directory.EnumerateFiles(trashPath)
                .Select(p =>
                {
                    var info = new FileInfo(p);
                    var m = MetaStore.EnsureFor(info.Name, info, meta);
                    // If it's in .trash folder but meta says not trashed, treat it as trashed.
                    if (!m.Trashed)
                    {
                        m = new MetaStore.FileMeta(m.UploadedUtc, m.Starred, Trashed: true, TrashedUtc: DateTime.UtcNow.ToString("o"));
                        meta[info.Name] = m;
                    }
                    return (object)MetaStore.ToDto(info.Name, info, m);
                });
            activeFiles.AddRange(trashFiles);
        }

        await MetaStore.SaveAsync(metaPath, meta);
        return Results.Ok(activeFiles);
    }
    finally
    {
        MetaStore.Lock.Release();
    }
});

// ── Star/unstar ────────────────────────────────────────────────────────────────
app.MapPost("/api/star/{fileName}", async (string fileName, [FromBody] JsonElement body) =>
{
    var safeName = SanitizeFileName(fileName);
    if (string.IsNullOrWhiteSpace(safeName))
        return Results.BadRequest("Invalid file name.");

    bool starred = body.ValueKind == JsonValueKind.Object &&
                  body.TryGetProperty("starred", out var v) &&
                  v.ValueKind is JsonValueKind.True or JsonValueKind.False
        ? v.GetBoolean()
        : true;

    await MetaStore.Lock.WaitAsync();
    try
    {
        var meta = await MetaStore.LoadAsync(metaPath);

        var activePath = Path.Combine(storagePath, safeName);
        var inTrashPath = Path.Combine(trashPath, safeName);
        var exists = File.Exists(activePath) || File.Exists(inTrashPath);
        if (!exists) return Results.NotFound();

        var info = new FileInfo(File.Exists(activePath) ? activePath : inTrashPath);
        var m = MetaStore.EnsureFor(safeName, info, meta);
        meta[safeName] = new MetaStore.FileMeta(m.UploadedUtc, Starred: starred, m.Trashed, m.TrashedUtc);
        await MetaStore.SaveAsync(metaPath, meta);
        return Results.Ok(new { name = safeName, starred });
    }
    finally
    {
        MetaStore.Lock.Release();
    }
}).DisableAntiforgery();

// ── Move to trash (soft delete) ────────────────────────────────────────────────
app.MapPost("/api/trash/{fileName}", async (string fileName) =>
{
    var safeName = SanitizeFileName(fileName);
    if (string.IsNullOrWhiteSpace(safeName))
        return Results.BadRequest("Invalid file name.");

    var from = Path.Combine(storagePath, safeName);
    if (!File.Exists(from)) return Results.NotFound();

    var to = Path.Combine(trashPath, safeName);
    if (File.Exists(to))
        to = GetUniquePath(trashPath, safeName);

    File.Move(from, to);

    var finalName = Path.GetFileName(to);

    await MetaStore.Lock.WaitAsync();
    try
    {
        var meta = await MetaStore.LoadAsync(metaPath);
        var info = new FileInfo(to);
        var m = MetaStore.EnsureFor(safeName, info, meta);

        // If name changed due to conflict, move meta entry as well.
        meta.Remove(safeName);
        meta[finalName] = new MetaStore.FileMeta(m.UploadedUtc, m.Starred, Trashed: true, TrashedUtc: DateTime.UtcNow.ToString("o"));

        await MetaStore.SaveAsync(metaPath, meta);
    }
    finally
    {
        MetaStore.Lock.Release();
    }

    return Results.Ok(new { name = finalName, trashed = true });
}).DisableAntiforgery();

// ── Restore from trash ─────────────────────────────────────────────────────────
app.MapPost("/api/restore/{fileName}", async (string fileName) =>
{
    var safeName = SanitizeFileName(fileName);
    if (string.IsNullOrWhiteSpace(safeName))
        return Results.BadRequest("Invalid file name.");

    var from = Path.Combine(trashPath, safeName);
    if (!File.Exists(from)) return Results.NotFound();

    var to = Path.Combine(storagePath, safeName);
    if (File.Exists(to))
        to = GetUniquePath(storagePath, safeName);

    File.Move(from, to);
    var finalName = Path.GetFileName(to);

    await MetaStore.Lock.WaitAsync();
    try
    {
        var meta = await MetaStore.LoadAsync(metaPath);
        var info = new FileInfo(to);
        var m = MetaStore.EnsureFor(safeName, info, meta);

        meta.Remove(safeName);
        meta[finalName] = new MetaStore.FileMeta(m.UploadedUtc, m.Starred, Trashed: false, TrashedUtc: null);
        await MetaStore.SaveAsync(metaPath, meta);
    }
    finally
    {
        MetaStore.Lock.Release();
    }

    return Results.Ok(new { name = finalName, trashed = false });
}).DisableAntiforgery();

// ── Permanently delete (only affects trash items) ──────────────────────────────
app.MapDelete("/api/trash/{fileName}", async (string fileName) =>
{
    var safeName = SanitizeFileName(fileName);
    if (string.IsNullOrWhiteSpace(safeName))
        return Results.BadRequest("Invalid file name.");

    var path = Path.Combine(trashPath, safeName);
    if (!File.Exists(path)) return Results.NotFound();

    File.Delete(path);

    await MetaStore.Lock.WaitAsync();
    try
    {
        var meta = await MetaStore.LoadAsync(metaPath);
        meta.Remove(safeName);
        await MetaStore.SaveAsync(metaPath, meta);
    }
    finally
    {
        MetaStore.Lock.Release();
    }

    return Results.NoContent();
});

// Encrypts an uploaded file and stores it as `<original>.enc` in the `storage` folder.
// Response includes `ivBase64` required for decryption, plus the `encryptedFileName` to download later.
app.MapPost("/api/encrypt", async ([FromForm] IFormFile file, [FromForm] string key) =>
{
    if (file is null || file.Length == 0)
        return Results.BadRequest("Missing file.");

    if (string.IsNullOrWhiteSpace(key))
        return Results.BadRequest("Missing key.");

    var safeOriginalName = SanitizeFileName(file.FileName);
    if (string.IsNullOrWhiteSpace(safeOriginalName))
        return Results.BadRequest("Invalid file name.");

    var encryptedFileName = $"{safeOriginalName}.enc";
    var encryptedPath = GetUniquePath(storagePath, encryptedFileName);

    // Derive a 256-bit AES key from the provided string (demo-friendly: avoids key-size mistakes).
    byte[] keyBytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));

    using var aes = Aes.Create();
    aes.KeySize = 256;
    aes.Mode = CipherMode.CBC;
    aes.Padding = PaddingMode.PKCS7;
    aes.Key = keyBytes;
    aes.GenerateIV();

    byte[] iv = aes.IV;

    await using var output = File.Create(encryptedPath);
    using var input = file.OpenReadStream();

    using var encryptor = aes.CreateEncryptor();
    using var cryptoStream = new CryptoStream(output, encryptor, CryptoStreamMode.Write);

    await input.CopyToAsync(cryptoStream);
    cryptoStream.FlushFinalBlock();

    var encryptedName = Path.GetFileName(encryptedPath);

    await MetaStore.Lock.WaitAsync();
    try
    {
        var meta = await MetaStore.LoadAsync(metaPath);
        meta[encryptedName] = new MetaStore.FileMeta(DateTime.UtcNow.ToString("o"), Starred: false, Trashed: false, TrashedUtc: null);
        await MetaStore.SaveAsync(metaPath, meta);
    }
    finally
    {
        MetaStore.Lock.Release();
    }

    return Results.Ok(new
    {
        encryptedFileName = encryptedName,
        ivBase64 = Convert.ToBase64String(iv),
        algorithm = "AES-CBC-PKCS7"
    });
}).DisableAntiforgery();

app.Run();

static class MetaStore
{
    internal record FileMeta(string UploadedUtc, bool Starred, bool Trashed, string? TrashedUtc);

    internal static readonly SemaphoreSlim Lock = new(1, 1);

    internal static async Task<Dictionary<string, FileMeta>> LoadAsync(string metaPath)
    {
        if (!File.Exists(metaPath)) return new(StringComparer.OrdinalIgnoreCase);

        try
        {
            var json = await File.ReadAllTextAsync(metaPath);
            var data = JsonSerializer.Deserialize<Dictionary<string, FileMeta>>(json) ?? new();
            return new Dictionary<string, FileMeta>(data, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    internal static async Task SaveAsync(string metaPath, Dictionary<string, FileMeta> meta)
    {
        var json = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(metaPath, json);
    }

    internal static FileMeta EnsureFor(string name, FileInfo info, Dictionary<string, FileMeta> meta)
    {
        if (meta.TryGetValue(name, out var existing)) return existing;

        // Best-effort "uploaded" time for pre-existing files.
        var uploadedUtc = info.CreationTimeUtc.ToString("o");
        var created = new FileMeta(uploadedUtc, Starred: false, Trashed: false, TrashedUtc: null);
        meta[name] = created;
        return created;
    }

    internal static object ToDto(string name, FileInfo info, FileMeta m)
    {
        return new
        {
            name,
            size = info.Length,
            modified = info.LastWriteTimeUtc.ToString("o"),
            uploaded = m.UploadedUtc,
            starred = m.Starred,
            trashed = m.Trashed,
            trashedAt = m.TrashedUtc
        };
    }
}