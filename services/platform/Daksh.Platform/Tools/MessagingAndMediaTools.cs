using System.Text.Json;
using Daksh.Platform.Channels;
using Daksh.Platform.Data;
using Daksh.Platform.Domain;
using Daksh.Platform.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Daksh.Platform.Tools.Core;

/// <summary>Queues an outbound message for the channel the current session is routed to, if any. Chat-only sessions have nothing to send to.</summary>
public sealed class MessageSendTool : ITool
{
    public ToolDescriptor Descriptor { get; } = new("message_send", "Sends a message on the channel this conversation is happening on (if any).", "messaging",
        Schemas.Obj(new { text = Schemas.Str("Message text to send.") }, ["text"]), ToolRisk.Medium, TimeoutSeconds: 15);

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement arguments, CancellationToken ct)
    {
        var db = context.Services.GetRequiredService<PlatformDbContext>();
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == context.SessionId, ct);
        if (session?.ChannelAccountId is null) return ToolResult.Fail("This session is not connected to an external channel.");
        db.ChannelMessages.Add(new ChannelMessage
        {
            WorkspaceId = context.WorkspaceId, ChannelAccountId = session.ChannelAccountId.Value, Direction = "out",
            ExternalMessageId = Guid.NewGuid().ToString("N"), ExternalChatId = session.PeerId ?? "",
            SessionId = session.Id, ExecutionId = context.ExecutionId, Status = "pending", Text = arguments.Required("text"),
        });
        await db.SaveChangesAsync(ct);
        return ToolResult.Success("Queued for delivery.");
    }
}

public sealed class ImageAnalyzeTool : ITool
{
    public ToolDescriptor Descriptor { get; } = new("image_analyze", "Analyzes a previously uploaded image using a vision-capable model.", "media",
        Schemas.Obj(new { file_id = Schemas.Str("Id of a stored image file."), question = Schemas.Str("What to look for or answer about the image.") },
            ["file_id", "question"]), ToolRisk.Low, TimeoutSeconds: 30);

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement arguments, CancellationToken ct)
    {
        var db = context.Services.GetRequiredService<PlatformDbContext>();
        if (!Guid.TryParse(arguments.Required("file_id"), out var fileId)) return ToolResult.Fail("file_id is not a valid id.");
        var file = await db.Files.FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file is null) return ToolResult.Fail("File not found.");
        if (!file.ContentType.StartsWith("image/")) return ToolResult.Fail("That file is not an image.");

        var storage = context.Services.GetRequiredService<Files.FileStorageService>();
        byte[] bytes;
        try { bytes = await storage.ReadAsync(file, ct); }
        catch (FileNotFoundException) { return ToolResult.Fail("The stored file is missing from disk."); }

        var db2 = context.Services.GetRequiredService<PlatformDbContext>();
        var agent = await db2.Agents.Include(a => a.Configuration).FirstAsync(a => a.Id == context.AgentId, ct);
        var modelRef = agent.Configuration?.DefaultModel ?? throw new InvalidOperationException("Agent has no default model.");
        var models = context.Services.GetRequiredService<ModelCallRunner>();

        var request = new ChatRequest(modelRef, [
            new ChatMessage(ChatRole.User, arguments.Required("question"), Parts: [new ContentPart("image", file.ContentType, Convert.ToBase64String(bytes))])]);
        ChatResponse? response = null;
        try
        {
            await foreach (var (evt, _) in models.StreamWithFallbackAsync(context.WorkspaceId, [modelRef], request, ct))
                if (evt is ChatCompleted c) response = c.Response;
        }
        catch (CapabilityException ex) { return ToolResult.Fail(ex.Message); }
        catch (NoEligibleModelException ex) { return ToolResult.Fail(ex.Message); }
        return ToolResult.Success(response?.Text ?? "The model returned no answer.");
    }
}
