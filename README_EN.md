# WebCoding -- A Command-Line Tool That Lets a Web-Based LLM Control Your Computer

English | [简体中文](README.md)

## Support

Windows 10/11 x64

Switchable between Chinese and English

## Installation

Download `webcoding for win.zip` from [Releases](https://github.com/zfyzfyzfyzfy666/WebCoding/releases/latest), unzip it, and double-click `WebCoding-Setup.exe` to open the paged installation wizard. If you just want to try it out, you can skip the ngrok installation.

In any project terminal, type `webcoding` to map the current directory and display/copy your private link.

## Quick Start

```powershell
webcoding                         # current directory
```

This generates a tunnel; the MCP web-based AI uses it to read and write files through the 12 tools provided by this project.

Ctrl+C closes the tunnel.

![](images/quickstart-log.png)

As shown above, it also displays a log of tool calls.

### Known to work with these (2026.10.02)

#### Arena.ai (works out of the box, completely free)

Copy the link as shown in the image.

![](images/arena-ai.png)

It has relatively new models, but you can't select the model directly, so the experience isn't smooth.

#### grok.com (just the free tier, also zero config)

![](images/grok.png)



#### chatgpt.com (requires at least a Plus subscription, needs a few setup steps)

![](images/chatgpt-1.png)

![](images/chatgpt-2.png)

![](images/chatgpt-3.png)



###  

## Advanced Configuration:

### ngrok (more recommended; just register if not configured) && cloudflare (zero config; slightly less stable in mainland China environments)





```powershell
webcoding                         # current directory
webcoding "D:\my-project"         # specified directory
webcoding --config                # edit installation rules and tool settings
webcoding --uninstall             # start interactive uninstall
webcoding --local-only            # do not start the public tunnel
webcoding --no-clipboard          # do not auto-copy the link
```

Running `webcoding` alone

shows 4 options:

```text
[1] ngrok
[2] Cloudflare auto (QUIC → HTTP/2)
[3] Cloudflare QUIC
[4] Cloudflare HTTP/2
```

The first choice is automatically saved as the default. The next time you run `webcoding`, it shows the current default and auto-starts after about 1.5 seconds; during that window, press `1/2/3/4` to temporarily switch the route for that run only.

### ngrok first-time authentication

Even ngrok's free tier requires an account and an authtoken. After installation you can choose to configure it immediately; if you skipped it then, WebCoding will automatically enter the guide the first time you choose ngrok:

1. It automatically opens ngrok's official token page;
2. The user registers/logs in and copies the authtoken;
3. Back in the WebCoding terminal, paste the token directly;
4. The input is displayed as `*`, and WebCoding automatically calls ngrok to complete the configuration and runs `config check`;
5. After successful configuration, it continues starting the tunnel.

No need to type `ngrok config add-authtoken ...` yourself. WebCoding does not write the token into its own config, logs, or installation info; the authentication config is maintained by ngrok itself.

### Cloudflare configuration options

```powershell
webcoding --protocol http2         # force HTTP/2, recommended first to compare network stability
webcoding --protocol prefer-http2  # prefer HTTP/2, fall back to QUIC if startup fails
webcoding --protocol quic          # force QUIC, for comparison
webcoding --protocol auto          # original default behavior: QUIC → HTTP/2
webcoding "D:\my-project" --protocol=http2
```



### agent.md (editable) as system prompt

- Installation rules: `%LOCALAPPDATA%\WebCoding\agent.md`.
- Project rules: `agent.md` at the root of the currently mapped directory.
- On first mapping, the template is created only if the project file does **not** exist; existing files are never overwritten.
- The service sends "built-in safety notes + installation rules + project rules" in the MCP `initialize.instructions`; if installation and project rules conflict, it asks the client to prefer the project rules.
- CF only forwards requests and responses; it does not deploy a separate enforced system prompt on the CF side. The client decides how to adopt the MCP instructions and cannot override its higher-priority system rules.
- After modifying rules, reconnect the client; after modifying `tools.json`, restart the mapping and then reconnect.

### 12 available tools; the root JSON is editable

| Tool                    | Responsibility                                     |
| --------------------- | -------------------------------------- |
| `list_directory`      | List directories, optionally recursive; up to 2000 items, 20 levels                |
| `read_files`          | Batch-read UTF-8 text with optional line ranges; up to 20 files, 4 MiB total output |
| `write_file`          | Create or overwrite a file, using a temp-file replacement                       |
| `edit_file`           | Single exact-match replacement; rejects no-match or multiple-match                   |
| `search_files`        | Literal search of file names or contents, glob filtering, pagination                |
| `manage_files`        | mkdir / move / rename / confirmed soft delete        |
| `run_command`         | Short PowerShell command; 60s default, 120s max per call     |
| `start_process`       | Start a foreground long-running task owned by the current mapping, e.g. a dev server                  |
| `read_process_output` | Incremental logs, wait for output/exit, list this mapping's tasks                  |
| `stop_process`        | Stop only tasks created by this service and their descendants; does not accept arbitrary system PIDs            |
| `read_image`          | Convert in-project PNG/JPEG/BMP/GIF into real MCP image content     |
| `capture_window`      | Enumerate windows or capture a specified target, returning real MCP image content              |

## Some Notes

### Search, soft delete, and processes

Search ignores hidden items and directories such as `.git`, `node_modules`, `dist`, `build`, `.next`, `.cache`, `.webcoding-trash` by default. Use `include_hidden` / `include_ignored` to explicitly include them. Search is literal matching and does not expose user regex; glob supports `*`, `**`, `?`. By default it scans up to 10000 files and collects 1000 results; a single scan lasts at most about 15 seconds, 20 levels deep. Reaching a limit or skipping something is reported. Pagination snapshots are kept for 5 minutes, up to 8 of them; after expiry, re-search — there is no guarantee file contents haven't changed between pages.

`manage_files delete` requires `confirm_delete=true`, and non-empty directories also require `recursive=true`. Deletion only moves items into the **project's own** `.webcoding-trash`, not the Windows Recycle Bin, and does not auto-clean. It returns the original location and the restore path; use `move` to restore. Overwriting the target or deleting the mapping root is forbidden. It's recommended to add `.webcoding-trash/` to the project's ignore list; full cleanup must be done separately by the user.

Persistent tasks return a random `process_id`. To read logs, use the returned absolute character `cursor`; when old logs are pushed out of the buffer, this is reported via `dropped_chars`. By default, up to 4 tasks run simultaneously, with 200,000 characters of log per task. Up to 64 task records are kept; finished records are evicted by time/capacity when new tasks are created. Tasks should run in the foreground and must not detach from the parent process; when a task ends, is stopped, or the mapping closes, its subtree is cleaned up. On Windows, each task uses a Job Object; the helper process only allows command execution after the task has been assigned, and it does not use an interface that "kills system processes by arbitrary PID."

### Images and window capture

```json
{"action":"list","title_contains":"my-app"}
{"action":"capture","hwnd":"0x123456","method":"printwindow"}
```

Windows support filtering by hwnd / PID / process name / title substring; multiple matches return candidates rather than choosing arbitrarily. Only visible top-level windows with titles are enumerated; minimized windows can be enumerated but are not restored by default.

- By default, it does not activate, restore, or switch windows.
- Only `allow_activate=true` permits restoring/bringing forward; after the operation it does not automatically switch back to the previous window.
- `method=screen` must explicitly allow activation and verify the target is actually in the foreground and fully within the desktop area; it refuses to silently capture another foreground window.
- `method=auto` tries PrintWindow first; it only tries the screen method when activation is allowed and detection fails or is near-blank.
- GPU/WebView2, protected content, and special rendering programs may appear black or partially missing; not all windows can be captured reliably, and near-blank detection is only a heuristic check.
- Screen captures may include always-on-top overlays; please only select authorized targets.
- Returns a `text` metadata block + an `image` (PNG base64) block, **not just a local path**. The client must support image tool results; different clients are not guaranteed to show them to the model.
- Input images up to 16 MiB, 40 megapixels; GIF reads only the first frame; output defaults to a max long edge of 1600 and up to 6 MiB, shrinking further if over the limit. Original images are not modified.
