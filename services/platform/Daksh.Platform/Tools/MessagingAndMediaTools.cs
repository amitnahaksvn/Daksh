using System.Text.Json;

namespace Daksh.Platform.Tools.Core;

// Pending MongoDB rebuild: previously queried PlatformDbContext.Sessions/ChannelMessages/Files/Agents.
/// <summary>Queues an outbound message for the channel the current session is routed to, if any. Chat-only sessions have nothing to send to.</summary>
public sealed class MessageSendTool : ITool
{
    public ToolDescriptor Descriptor { get; } = new("message_send", "Sends a message on the channel this conversation is happening on (if any).", "messaging",
        Schemas.Obj(new { text = Schemas.Str("Message text to send.") }, ["text"]), ToolRisk.Medium, TimeoutSeconds: 15);

    public Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement arguments, CancellationToken ct) =>
        Task.FromResult(ToolResult.Fail("Message send is unavailable: pending MongoDB rebuild."));
}

public sealed class ImageAnalyzeTool : ITool
{
    public ToolDescriptor Descriptor { get; } = new("image_analyze", "Analyzes a previously uploaded image using a vision-capable model.", "media",
        Schemas.Obj(new { file_id = Schemas.Str("Id of a stored image file."), question = Schemas.Str("What to look for or answer about the image.") },
            ["file_id", "question"]), ToolRisk.Low, TimeoutSeconds: 30);

    public Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement arguments, CancellationToken ct) =>
        Task.FromResult(ToolResult.Fail("Image analysis is unavailable: pending MongoDB rebuild."));
}
