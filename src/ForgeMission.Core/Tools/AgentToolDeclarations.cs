using System.Text.Json;
using Katasec.AITools;
using Microsoft.Extensions.AI;

namespace ForgeMission.Core.Tools;

// The Read/Edit/Write/Bash tool declarations CapabilityRegistry advertises for the file and
// terminal capability providers it holds. Forge originates these itself; there is no upstream
// client tool list to relay.
//
// Schema content is copied from the real `claude` CLI's captured wire traffic, not invented: no
// official SDK publishes what these four tools look like, only the generic AITool envelope to
// declare any tool in. Trimmed to what the workspace capabilities support — Bash drops
// timeout/run_in_background/dangerouslyDisableSandbox (already unrestricted, no sandbox toggle to
// expose); Read drops the PDF-only `pages` field; Edit and Write are unchanged.
//
// Hand-written, not AIFunctionFactory.Create(delegate): that overload derives its schema via
// reflection, unsafe in the AOT-published binary. DeclaredTool (Katasec.AITools) is the shared
// declaration-only AIFunction shape, so every declaration is built the same way.
public static class AgentToolDeclarations
{
    public static readonly AIFunction Read = new DeclaredTool("Read",
        "Reads a file from the local filesystem.",
        ParseSchema("""
        {
          "type": "object",
          "properties": {
            "file_path": { "type": "string",  "description": "The absolute path to the file to read" },
            "offset":    { "type": "integer", "description": "The line number to start reading from. Only provide if the file is too large to read at once" },
            "limit":     { "type": "integer", "description": "The number of lines to read. Only provide if the file is too large to read at once" }
          },
          "required": ["file_path"],
          "additionalProperties": false
        }
        """));

    public static readonly AIFunction Edit = new DeclaredTool("Edit",
        "Performs an exact string replacement in a file.",
        ParseSchema("""
        {
          "type": "object",
          "properties": {
            "file_path":   { "type": "string",  "description": "The absolute path to the file to modify" },
            "old_string":  { "type": "string",  "description": "The text to replace" },
            "new_string":  { "type": "string",  "description": "The text to replace it with (must be different from old_string)" },
            "replace_all": { "type": "boolean", "description": "Replace all occurrences of old_string (default false)", "default": false }
          },
          "required": ["file_path", "old_string", "new_string"],
          "additionalProperties": false
        }
        """));

    public static readonly AIFunction Write = new DeclaredTool("Write",
        "Writes a file to the local filesystem. Overwrites the file if it already exists.",
        ParseSchema("""
        {
          "type": "object",
          "properties": {
            "file_path": { "type": "string", "description": "The absolute path to the file to write" },
            "content":   { "type": "string", "description": "The content to write to the file" }
          },
          "required": ["file_path", "content"],
          "additionalProperties": false
        }
        """));

    public static readonly AIFunction Bash = new DeclaredTool("Bash",
        "Executes a shell command and returns its output.",
        ParseSchema("""
        {
          "type": "object",
          "properties": {
            "command": { "type": "string", "description": "The command to execute" }
          },
          "required": ["command"],
          "additionalProperties": false
        }
        """));

    private static JsonElement ParseSchema(string json) => JsonDocument.Parse(json).RootElement;
}
