using System.Text.Json.Nodes;

namespace CsAgent.Core.Agent;

public static partial class ToolDispatcher
{
    public static readonly JsonArray ToolDefinitions = JsonNode.Parse("""
        [
          {
            "type": "function",
            "function": {
              "name": "write_file",
              "description": "Write (or overwrite) a text file. Parent directories are created automatically.",
              "parameters": {
                "type": "object",
                "properties": {
                  "path":    { "type": "string", "description": "File path." },
                  "content": { "type": "string", "description": "UTF-8 content to write." }
                },
                "required": ["path", "content"]
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "read_file",
              "description": "Read a text file and return its content.",
              "parameters": {
                "type": "object",
                "properties": {
                  "path": { "type": "string", "description": "File path." }
                },
                "required": ["path"]
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "read_json",
              "description": "Read a JSON file and return it as pretty-printed JSON. Optionally provide a 'query' (a dot-path like 'a.b[0].c') to extract just a sub-value. Use this to inspect structured data files (config, package.json, lockfiles, etc.).",
              "parameters": {
                "type": "object",
                "properties": {
                  "path":  { "type": "string", "description": "Path of the JSON file to read." },
                  "query": { "type": "string", "description": "Optional dot-path to extract a sub-value, e.g. 'dependencies.react' or 'scripts[0]'." }
                },
                "required": ["path"]
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "list_dir",
              "description": "List files and subdirectories in a directory.",
              "parameters": {
                "type": "object",
                "properties": {
                  "path":      { "type": "string",  "description": "Directory to list. Defaults to '.'." },
                  "recursive": { "type": "boolean", "description": "Whether to list recursively." }
                },
                "required": []
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "tree",
              "description": "Display a visual, indented directory tree of the given path. Directories are shown with a trailing '/'. Use 'depth' to limit how many levels deep to recurse (-1 for unlimited). Hidden directories (starting with '.') and build output (bin/, obj/) are skipped.",
              "parameters": {
                "type": "object",
                "properties": {
                  "path":  { "type": "string",  "description": "Directory to display. Defaults to '.'." },
                  "depth": { "type": "integer", "description": "Maximum recursion depth. -1 (default) means unlimited." }
                },
                "required": []
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "search_files",
              "description": "Recursively search for a text pattern (grep) inside files under a directory. Returns matching file paths and line numbers. Use this to find where symbols, strings, or code are referenced.",
              "parameters": {
                "type": "object",
                "properties": {
                  "pattern": { "type": "string", "description": "The literal text or substring to search for (case-insensitive)." },
                  "path":    { "type": "string", "description": "Directory to search. Defaults to '.'." },
                  "glob":    { "type": "string", "description": "Optional file glob filter, e.g. '*.cs' or '*.js'. Defaults to '*'." }
                },
                "required": ["pattern"]
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "edit_file",
              "description": "Apply precise find-and-replace edits to an existing text file without rewriting the whole file. Provide an array of edits, each with an 'old_string' (exact text to find, must appear exactly once) and a 'new_string' (replacement). All edits are applied atomically; if any edit fails, no changes are written.",
              "parameters": {
                "type": "object",
                "properties": {
                  "path": { "type": "string", "description": "File path to edit." },
                  "edits": {
                    "type": "array",
                    "description": "List of edits to apply. Each edit replaces an exact old_string with a new_string.",
                    "items": {
                      "type": "object",
                      "properties": {
                        "old_string": { "type": "string", "description": "Exact text to find. Must appear exactly once in the file." },
                        "new_string": { "type": "string", "description": "Replacement text." }
                      },
                      "required": ["old_string", "new_string"]
                    }
                  }
                },
                "required": ["path", "edits"]
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "copy_file",
              "description": "Copy a file from source to destination. Parent directories of the destination are created automatically.",
              "parameters": {
                "type": "object",
                "properties": {
                  "source":      { "type": "string", "description": "Path of the file to copy." },
                  "destination": { "type": "string", "description": "Destination path for the copy." }
                },
                "required": ["source", "destination"]
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "move_file",
              "description": "Move (rename) a file from source to destination. Parent directories of the destination are created automatically. Destructive — requires user confirmation.",
              "parameters": {
                "type": "object",
                "properties": {
                  "source":      { "type": "string", "description": "Path of the file to move." },
                  "destination": { "type": "string", "description": "Destination path for the move." }
                },
                "required": ["source", "destination"]
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "delete_file",
              "description": "Permanently delete a file. Destructive — requires user confirmation.",
              "parameters": {
                "type": "object",
                "properties": {
                  "path": { "type": "string", "description": "Path of the file to delete." }
                },
                "required": ["path"]
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "zip",
              "description": "Create a zip archive from a source file or directory. If the source is a directory, its contents are archived recursively.",
              "parameters": {
                "type": "object",
                "properties": {
                  "source":      { "type": "string", "description": "File or directory to archive." },
                  "destination": { "type": "string", "description": "Path of the .zip archive to create." }
                },
                "required": ["source", "destination"]
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "unzip",
              "description": "Extract a zip archive into a destination directory. The destination directory is created if it does not exist. Destructive — overwrites existing files, requires user confirmation.",
              "parameters": {
                "type": "object",
                "properties": {
                  "archive":     { "type": "string", "description": "Path of the .zip archive to extract." },
                  "destination": { "type": "string", "description": "Directory to extract into." }
                },
                "required": ["archive", "destination"]
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "parse_output",
              "description": "Parse a block of command output into structured data and return it as pretty-printed JSON. Use 'format' to hint the format: 'json' (parse as JSON), 'keyvalue' (parse 'key=value' or 'key: value' lines), 'csv' (parse comma/tab-separated rows), or 'auto' (default, auto-detect). Optionally provide a 'query' (dot-path) to extract just a sub-value from the parsed result.",
              "parameters": {
                "type": "object",
                "properties": {
                  "output": { "type": "string", "description": "The raw command output text to parse." },
                  "format": { "type": "string", "description": "Parsing format: 'json', 'keyvalue', 'csv', or 'auto' (default)." },
                  "query":  { "type": "string", "description": "Optional dot-path to extract a sub-value from the parsed result." }
                },
                "required": ["output"]
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "git_status",
              "description": "Show the working tree status (modified, staged, untracked files) of the git repository containing the given path.",
              "parameters": {
                "type": "object",
                "properties": {
                  "path": { "type": "string", "description": "Directory inside the git repo. Defaults to '.'." }
                },
                "required": []
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "git_diff",
              "description": "Show uncommitted changes. By default shows unstaged changes; set 'staged' to true to show staged (index) changes.",
              "parameters": {
                "type": "object",
                "properties": {
                  "path":  { "type": "string", "description": "Directory inside the git repo. Defaults to '.'." },
                  "staged": { "type": "boolean", "description": "If true, show staged changes instead of unstaged. Defaults to false." }
                },
                "required": []
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "git_log",
              "description": "Show the recent commit history of the git repository.",
              "parameters": {
                "type": "object",
                "properties": {
                  "path":  { "type": "string", "description": "Directory inside the git repo. Defaults to '.'." },
                  "count": { "type": "integer", "description": "Number of commits to show. Defaults to 20." }
                },
                "required": []
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "git_branch",
              "description": "Show the current branch and list all local branches of the git repository.",
              "parameters": {
                "type": "object",
                "properties": {
                  "path": { "type": "string", "description": "Directory inside the git repo. Defaults to '.'." }
                },
                "required": []
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "git_commit",
              "description": "Stage all changes and create a commit with the given message. Destructive — requires user confirmation.",
              "parameters": {
                "type": "object",
                "properties": {
                  "path":    { "type": "string", "description": "Directory inside the git repo. Defaults to '.'." },
                  "message": { "type": "string", "description": "Commit message." }
                },
                "required": ["message"]
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "sh",
              "description": "Execute a shell command. Uses cmd.exe on Windows, /bin/sh elsewhere.",
              "parameters": {
                "type": "object",
                "properties": {
                  "cmd": { "type": "string", "description": "Shell command to run." }
                },
                "required": ["cmd"]
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "http_request",
              "description": "Make an HTTP request to a URL and return the status, headers, and body. Use this to call web APIs or fetch remote resources.",
              "parameters": {
                "type": "object",
                "properties": {
                  "url":       { "type": "string", "description": "The absolute http/https URL to request." },
                  "method":    { "type": "string", "description": "HTTP method: GET, POST, PUT, PATCH, DELETE, etc. Defaults to GET." },
                  "headers":   { "type": "object", "description": "Optional request headers as a JSON object of string values." },
                  "body":      { "type": "string", "description": "Optional request body (sent for POST/PUT/PATCH)." },
                  "timeoutMs": { "type": "integer", "description": "Timeout in milliseconds. Defaults to 30000, max 120000." }
                },
                "required": ["url"]
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "web_search",
              "description": "Search the web for docs, errors, or solutions. Returns a list of ranked results with titles, URLs, and snippets.",
              "parameters": {
                "type": "object",
                "properties": {
                  "query":      { "type": "string",  "description": "The search query text." },
                  "maxResults": { "type": "integer", "description": "Maximum number of results to return. Defaults to 5, max 10." }
                },
                "required": ["query"]
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "fetch_url",
              "description": "Retrieve the content of a webpage or URL and return it as readable text. Use this to read articles, docs, or any web page. Returns the page title and the visible text content (HTML tags stripped).",
              "parameters": {
                "type": "object",
                "properties": {
                  "url":      { "type": "string",  "description": "The absolute http/https URL of the page to fetch." },
                  "maxChars": { "type": "integer", "description": "Maximum number of characters of text to return. Defaults to 20000, max 100000." }
                },
                "required": ["url"]
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "run_terminal",
              "description": "Run a command in an interactive, persistent shell session. State (current directory, environment variables, etc.) is preserved between calls to the same session. Use this for long-running or stateful workflows (e.g. starting a dev server, running a REPL, or chaining commands that depend on prior state). Each session keeps its own shell process alive until closed with close_terminal.",
              "parameters": {
                "type": "object",
                "properties": {
                  "cmd":       { "type": "string",  "description": "The command to run in the session's shell." },
                  "session":   { "type": "string",  "description": "Optional session id. Defaults to 'default'. Use distinct ids for independent sessions." },
                  "timeoutMs": { "type": "integer", "description": "Timeout in milliseconds to wait for output. Defaults to 60000, max 300000." }
                },
                "required": ["cmd"]
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "close_terminal",
              "description": "Close and terminate a persistent terminal session created by run_terminal, releasing its shell process. Use this when you are done with a session to free resources.",
              "parameters": {
                "type": "object",
                "properties": {
                  "session": { "type": "string", "description": "Optional session id to close. Defaults to 'default'." }
                },
                "required": []
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "switch_model",
              "description": "Switch the active LLM model for the current session. Use this when the user asks to change or switch the model.",
              "parameters": {
                "type": "object",
                "properties": {
                  "model": { "type": "string", "description": "The model identifier to switch to (e.g. 'openai/gpt-oss-120b')." }
                },
                "required": ["model"]
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "list_models",
              "description": "List all LLM models available on the Albert API endpoint, with their type and aliases. Use this when the user asks what models are available.",
              "parameters": {
                "type": "object",
                "properties": {},
                "required": []
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "read_clipboard",
              "description": "Read the current text content of the system clipboard. Use this when the user asks to read, analyse, fix, complete, or act on whatever is currently in the clipboard.",
              "parameters": {
                "type": "object",
                "properties": {},
                "required": []
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "write_clipboard",
              "description": "Write text to the system clipboard, replacing its current content. Use this to send corrected, completed, or generated code/text back to the clipboard so the user can paste it. Destructive — requires confirmation.",
              "parameters": {
                "type": "object",
                "properties": {
                  "content": {
                    "type": "string",
                    "description": "The text to place in the clipboard."
                  }
                },
                "required": ["content"]
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "excel_command",
              "description": "Run a PowerShell command against a live, persistent Excel automation session. Windows only. The first call in a session launches Excel via COM (reusing an already-running instance if one exists) and prepares $excel (the Application), $wb (a workbook), and $ws (its first worksheet) — these variables remain available in every subsequent call to the same session, so multi-step interaction (set values, apply formulas, format cells, add charts, save) works naturally across many calls without losing state. The command runs as raw PowerShell, so it can reference and mutate $excel/$wb/$ws directly, e.g. \"$ws.Cells.Item(1,1) = 'Hello'\" or \"$wb.SaveAs('C:\\\\path\\\\out.xlsx')\". Use close_excel when done with a session.",
              "parameters": {
                "type": "object",
                "properties": {
                  "command":   { "type": "string",  "description": "A PowerShell command or statement to run in this session, typically referencing $excel, $wb, and/or $ws." },
                  "session":   { "type": "string",  "description": "Optional session id. Defaults to 'default'. IMPORTANT: reuse the SAME session id across every call that is part of one continuous task, so $excel/$wb/$ws carry over between steps — do not invent a new id per sub-step. Only use a distinct id when you genuinely want a second, independent Excel window running in parallel to the first." },
                  "timeoutMs": { "type": "integer", "description": "Timeout in milliseconds to wait for the command's output. Defaults to 60000, max 300000." }
                },
                "required": ["command"]
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "close_excel",
              "description": "Close a persistent Excel automation session created by excel_command, releasing its PowerShell driver process. Excel itself is left running (it was placed in user-controlled mode), so any open workbook stays visible and usable — this only ends the automation connection, not the application.",
              "parameters": {
                "type": "object",
                "properties": {
                  "session": { "type": "string", "description": "Optional session id to close. Defaults to 'default'." }
                },
                "required": []
              }
            }
          },
          {
            "type": "function",
            "function": {
              "name": "send_email",
              "description": "Compose an email via Microsoft Outlook COM automation and either display it as a draft for the user to review (default), save it to drafts, or send it immediately. Windows only, requires classic desktop Outlook installed and configured. Destructive — actually sending an email is irreversible, so this tool always requires user confirmation regardless of the 'send' value.",
              "parameters": {
                "type": "object",
                "properties": {
                  "to":           { "type": "string",  "description": "Recipient email address(es), semicolon-separated for multiple." },
                  "cc":           { "type": "string",  "description": "Optional CC address(es), semicolon-separated." },
                  "bcc":          { "type": "string",  "description": "Optional BCC address(es), semicolon-separated." },
                  "subject":      { "type": "string",  "description": "Email subject line." },
                  "body":         { "type": "string",  "description": "Plain-text body. Ignored if 'bodyHtml' is provided." },
                  "bodyHtml":     { "type": "string",  "description": "HTML body, takes priority over 'body' if both are given." },
                  "attachments":  { "type": "array",   "description": "Optional list of file paths to attach. Each must exist on disk; the request fails if any attachment is not found.", "items": { "type": "string" } },
                  "send":         { "type": "boolean", "description": "If true, actually sends the email immediately (irreversible). Defaults to false." },
                  "saveToDrafts": { "type": "boolean", "description": "If true (and 'send' is false), saves the email to Outlook drafts instead of just displaying it. Defaults to false." }
                },
                "required": ["to", "subject"]
              }
            }
          }
        ]
        """)!.AsArray();
}