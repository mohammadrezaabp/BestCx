using Cx.Application.Abstractions;
using Cx.Application.Operations;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Cx.Infrastructure.Operations;

public sealed class OperationsErrorFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try { return await next(context); }
        catch (OperationsException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.Status); }
        catch (PostgresException ex) when (ex.SqlState is "23505" or "23503" or "23502" or "23514")
        { return Results.Json(new { error = ex.SqlState == "23505" ? "This name or record has already been registered." : "The associated information or record is not valid." }, statusCode: ex.SqlState == "23505" ? 409 : 400); }
    }
}

public static class OperationsHttp
{
    public static async Task<IResult> UploadAsync(HttpRequest request, OperationsStore store, IObjectStorage storage,
        string tenant, string actor, Guid caseId, string? customer, CancellationToken ct)
    {
        if (!request.HasFormContentType) return Results.BadRequest(new { error = "Select a file." });
        if (request.ContentLength > 10485760 + 65536) return Results.Json(new { error = "The maximum file size is 10 MB." }, statusCode: 413);
        var form = await request.ReadFormAsync(ct);
        if (form.Files.Count != 1 || !Guid.TryParse(form["attachment_id"], out var id) || id == Guid.Empty) return Results.BadRequest(new { error = "The file and submission ID are not valid." });
        var existing = await store.AttachmentAsync(tenant, caseId, id, customer, ct);
        if (existing is not null) return Results.Ok(existing);
        var file = form.Files[0];
        if (file.Length is < 1 or > 10485760) return Results.Json(new { error = "The size of the file must be between 1 byte and 10 megabytes." }, statusCode: 413);
        var filename = Path.GetFileName(file.FileName.Replace('\\', '/'));
        filename = string.Concat(filename.Where(character => !char.IsControl(character)));
        if (string.IsNullOrWhiteSpace(filename) || filename.Length > 256) throw new OperationsException("The file name is not valid.");
        var key = $"cx/{Uri.EscapeDataString(tenant)}/cases/{caseId}/{id}/{Guid.NewGuid():N}";
        await using var stream = file.OpenReadStream();
        var write = await storage.PutStreamAsync(key, stream, "application/octet-stream", file.Length, ct);
        var attachment = new CaseAttachment { Id = id, CaseId = caseId, Filename = filename, ObjectKey = write.Key,
            ContentType = "application/octet-stream", SizeBytes = write.SizeBytes, Public = customer is not null || form["public"] == "true", UploadedBy = actor };
        try { await store.SaveAttachmentAsync(tenant, actor, caseId, attachment, customer, ct); }
        catch
        {
            await storage.DeleteAsync(write.Key, CancellationToken.None);
            var duplicate = await store.AttachmentAsync(tenant, caseId, id, customer, ct);
            if (duplicate is not null) return Results.Ok(duplicate);
            throw;
        }
        return Results.Ok(attachment);
    }
    public static async Task<IResult> DownloadAsync(OperationsStore store, IObjectStorage storage, string tenant,
        Guid caseId, Guid id, string? customer, CancellationToken ct)
    {
        var attachment = await store.AttachmentAsync(tenant, caseId, id, customer, ct);
        if (attachment is null) return Results.NotFound();
        var stream = await storage.OpenReadAsync(attachment.ObjectKey, ct);
        return Results.File(stream, "application/octet-stream", attachment.Filename, enableRangeProcessing: false);
    }
    public static (DateTimeOffset From, DateTimeOffset To) Range(DateTimeOffset? from, DateTimeOffset? to)
    {
        var end = to ?? DateTimeOffset.UtcNow;
        var start = from ?? end.AddDays(-30);
        if (start >= end || (end - start).TotalDays > 366) throw new OperationsException("The interval must be valid and maximum 366 days.");
        return (start, end);
    }
}
