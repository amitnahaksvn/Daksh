using System.Text;
using Daksh.Platform.Security;

namespace Daksh.Platform.Tools.Core;

internal static class Schemas
{
    public static System.Text.Json.JsonElement Obj(object properties, string[]? required = null) =>
        JsonDefaults.Schema(new { type = "object", properties, required = required ?? [], additionalProperties = false });
    public static readonly object StringProp = new { type = "string" };
    public static object Str(string description) => new { type = "string", description };
    public static object Int(string description) => new { type = "integer", description };
    public static object Bool(string description) => new { type = "boolean", description };
}

public sealed class ReadFileTool : ITool
{
    public ToolDescriptor Descriptor { get; } = new("read_file", "Reads a text file from the agent's workspace.", "fs",
        Schemas.Obj(new { path = Schemas.Str("Path relative to the workspace root.") }, ["path"]),
        ToolRisk.Low, TimeoutSeconds: 15, Idempotent: true);

    public Task<ToolResult> ExecuteAsync(ToolContext context, System.Text.Json.JsonElement arguments, CancellationToken ct)
    {
        if (context.WorkspaceAccess == "none") return Task.FromResult(ToolResult.Fail("This agent has no file system access."));
        var jail = new PathJail(context.WorkspaceDirectory);
        var full = jail.Resolve(arguments.Required("path"));
        if (!File.Exists(full)) return Task.FromResult(ToolResult.Fail($"File '{arguments.Str("path")}' does not exist."));
        var info = new FileInfo(full);
        if (info.Length > 1_000_000) return Task.FromResult(ToolResult.Fail("File is larger than 1 MB; use a more specific tool or request a range."));
        return Task.FromResult(ToolResult.Success(File.ReadAllText(full, Encoding.UTF8)));
    }
}

public sealed class WriteFileTool : ITool
{
    public ToolDescriptor Descriptor { get; } = new("write_file", "Creates or overwrites a text file in the agent's workspace.", "fs",
        Schemas.Obj(new { path = Schemas.Str("Path relative to the workspace root."), content = Schemas.Str("File content.") }, ["path", "content"]),
        ToolRisk.Medium, TimeoutSeconds: 15);

    public Task<ToolResult> ExecuteAsync(ToolContext context, System.Text.Json.JsonElement arguments, CancellationToken ct)
    {
        if (context.WorkspaceAccess != "rw") return Task.FromResult(ToolResult.Fail("This agent has read-only (or no) file system access."));
        var jail = new PathJail(context.WorkspaceDirectory);
        var full = jail.Resolve(arguments.Required("path"));
        var content = arguments.Str("content") ?? "";
        if (Encoding.UTF8.GetByteCount(content) > 2_000_000) return Task.FromResult(ToolResult.Fail("Content is larger than 2 MB."));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, Encoding.UTF8);
        return Task.FromResult(ToolResult.Success($"Wrote {Encoding.UTF8.GetByteCount(content)} bytes to {arguments.Str("path")}."));
    }
}

public sealed class EditFileTool : ITool
{
    public ToolDescriptor Descriptor { get; } = new("edit_file", "Replaces one exact occurrence of text in a file.", "fs",
        Schemas.Obj(new
        {
            path = Schemas.Str("Path relative to the workspace root."),
            old_text = Schemas.Str("Exact text to find; must match exactly once."),
            new_text = Schemas.Str("Replacement text.")
        }, ["path", "old_text", "new_text"]), ToolRisk.Medium, TimeoutSeconds: 15);

    public Task<ToolResult> ExecuteAsync(ToolContext context, System.Text.Json.JsonElement arguments, CancellationToken ct)
    {
        if (context.WorkspaceAccess != "rw") return Task.FromResult(ToolResult.Fail("This agent has read-only (or no) file system access."));
        var jail = new PathJail(context.WorkspaceDirectory);
        var full = jail.Resolve(arguments.Required("path"));
        if (!File.Exists(full)) return Task.FromResult(ToolResult.Fail("File does not exist."));
        var text = File.ReadAllText(full, Encoding.UTF8);
        var oldText = arguments.Required("old_text");
        var count = CountOccurrences(text, oldText);
        if (count == 0) return Task.FromResult(ToolResult.Fail("old_text was not found in the file."));
        if (count > 1) return Task.FromResult(ToolResult.Fail($"old_text matches {count} times; it must match exactly once. Include more context."));
        File.WriteAllText(full, text.Replace(oldText, arguments.Str("new_text") ?? ""), Encoding.UTF8);
        return Task.FromResult(ToolResult.Success("Edit applied."));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (needle.Length == 0) return 0;
        int count = 0, index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) != -1) { count++; index += needle.Length; }
        return count;
    }
}

public sealed class ListFilesTool : ITool
{
    public ToolDescriptor Descriptor { get; } = new("list_files", "Lists files and directories in the agent's workspace.", "fs",
        Schemas.Obj(new { path = Schemas.Str("Directory relative to the workspace root. Defaults to the root.") }),
        ToolRisk.Low, TimeoutSeconds: 15, Idempotent: true);

    public Task<ToolResult> ExecuteAsync(ToolContext context, System.Text.Json.JsonElement arguments, CancellationToken ct)
    {
        if (context.WorkspaceAccess == "none") return Task.FromResult(ToolResult.Fail("This agent has no file system access."));
        var jail = new PathJail(context.WorkspaceDirectory);
        var full = jail.Resolve(arguments.Str("path") ?? ".");
        if (!Directory.Exists(full)) return Task.FromResult(ToolResult.Fail("Directory does not exist."));
        var entries = Directory.EnumerateFileSystemEntries(full).OrderBy(e => e).Take(500)
            .Select(e => (Directory.Exists(e) ? "d " : "f ") + jail.Relative(e));
        return Task.FromResult(ToolResult.Success(string.Join("\n", entries) is { Length: > 0 } s ? s : "(empty)"));
    }
}

public sealed class SearchFilesTool : ITool
{
    public ToolDescriptor Descriptor { get; } = new("search_files", "Searches file contents in the agent's workspace for a literal substring.", "fs",
        Schemas.Obj(new
        {
            query = Schemas.Str("Text to search for."),
            path = Schemas.Str("Directory to search within, relative to the workspace root. Defaults to the root."),
        }, ["query"]), ToolRisk.Low, TimeoutSeconds: 30, Idempotent: true);

    public Task<ToolResult> ExecuteAsync(ToolContext context, System.Text.Json.JsonElement arguments, CancellationToken ct)
    {
        if (context.WorkspaceAccess == "none") return Task.FromResult(ToolResult.Fail("This agent has no file system access."));
        var jail = new PathJail(context.WorkspaceDirectory);
        var root = jail.Resolve(arguments.Str("path") ?? ".");
        var query = arguments.Required("query");
        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (hits.Count >= 100 || ct.IsCancellationRequested) break;
            FileInfo info;
            try { info = new FileInfo(file); } catch (IOException) { continue; }
            if (info.Length > 2_000_000) continue;
            string[] lines;
            try { lines = File.ReadAllLines(file); } catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
            for (var i = 0; i < lines.Length && hits.Count < 100; i++)
                if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                    hits.Add($"{jail.Relative(file)}:{i + 1}: {lines[i].Trim()}");
        }
        return Task.FromResult(ToolResult.Success(hits.Count > 0 ? string.Join("\n", hits) : "No matches found."));
    }
}
