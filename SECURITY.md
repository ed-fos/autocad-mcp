# Security

Read this before installing. This tool is more dangerous than most MCP servers, and the
reasons are specific rather than boilerplate.

## What this thing actually is

Nodal.AcadMcp puts an HTTP endpoint **inside your running AutoCAD process** and lets a
language model drive it. Three of its 133 tools are unsandboxed code execution by design:

| Tool | What it can do |
|---|---|
| `run_csharp` | Compiles and runs arbitrary C# in-process, with AutoCAD's full privileges |
| `run_lisp` | Evaluates arbitrary AutoLISP — including `(startapp)` and `vlax-create-object` |
| `send_command` | Types anything into the AutoCAD command line |

These are not accidents or leftovers. A finite tool set cannot cover AutoCAD, and without an
escape hatch the model simply gives up on anything unusual. But it does mean that anything
able to reach this endpoint can read, overwrite or delete any file your Windows account can
reach, start processes, and change or destroy any open drawing.

## Threat model

### The one that matters most: persistence and propagation

The obvious worry is "someone runs code in my AutoCAD session". In this ecosystem the more
realistic payload is worse than that, and it is specific to CAD:

AutoCAD automatically loads `acad.lsp` / `acaddoc.lsp` and `.fas` files from its support
paths. Code written there **re-executes on every AutoCAD start, forever** — it survives
uninstalling this add-in, and because support paths and project folders travel with drawing
sets, it can reach the consultants and clients you send DWGs to. AutoCAD has a long history
of exactly this propagation pattern.

So the blast radius of a compromise here is not your session. It is your machine, and
potentially the people you exchange drawings with.

Mitigations in the product:

- `set_system_variable` **refuses** to write `SECURELOAD`, `TRUSTEDPATHS`, `TRUSTEDDOMAINS`,
  `ACADLSPASDOC`, `LISPSYS`, `AUTOLOAD` and `SECUREREMOTEACCESS`. Those govern what AutoCAD
  is willing to load and they persist in your user profile, so one tool call must not be able
  to permanently weaken AutoCAD's own defences. This is a guard rail, not a boundary —
  anything that can reach `run_lisp` can call `(setvar)` directly — but it means the default
  surface cannot be turned into persistence by a single call.
- Leave AutoCAD's own `SECURELOAD` at its default. This tool does not need it lowered.

### Browser reachability (CSRF and DNS rebinding)

Binding to loopback is **not** sufficient protection for a local endpoint, and this is the
failure mode that turns a reasonable personal tool into an unreasonable published one:

- Any web page you visit can `fetch()` a cross-origin POST at `127.0.0.1`. With a
  `text/plain` body that request is CORS-simple, so no preflight fires and the call lands.
  The attacker cannot read the reply — but for `run_csharp` the payload *is* the attack.
- **DNS rebinding** goes further: the attacker's own domain re-resolves to `127.0.0.1`, which
  makes the request same-origin, so the reply is readable too. That is a full interactive
  shell driven from a web page, with your drawing contents coming back out.

Four checks stand in front of dispatch ([`HttpRequestGuard`](src/Nodal.AcadMcp.Contracts/HttpRequestGuard.cs)):

1. **A per-session token.** Minted when the bridge starts, shared only through a file under
   `%LOCALAPPDATA%` (per-user, so another Windows account cannot read it). Compared in fixed
   time. This is the actual boundary — it stops any other local process, not just browsers.
2. **`Host` must be loopback and the right port.** This is what defeats rebinding.
3. **Any `Origin` or `Referer` is rejected outright.** A real MCP client never sends either.
4. **POST must declare `application/json`**, which makes the request non-CORS-simple, so a
   browser has to preflight — and the preflight fails, because no CORS headers are served.

These are covered by regression tests in
[`tests/Nodal.AcadMcp.Tests`](tests/Nodal.AcadMcp.Tests/HttpRequestGuardTests.cs). If you
change `HttpRequestGuard`, those tests are the contract.

### The bridge is opt-in, per session

The add-in loads when AutoCAD starts, but **loading is not listening**. `Initialize()` opens no
socket and accepts no input — it registers the commands and the ribbon panel, nothing more.
Nothing listens until you click **Start** on the Nodal MCP ribbon panel or type
`NODALMCPSTART`. `NODALMCPSTOP` closes it and invalidates the token.

The bundle briefly shipped `LoadOnAutoCADStartup="false"`, mirroring Autodesk's own MCP
bundle, on the theory that not loading at all was stricter. Testing showed it simply breaks:
with the module unloaded AutoCAD has no idea `NODALMCPSTART` exists, so the command meant to
demand-load it reports *Unknown command* and a clean install cannot be started. Demand-loading
a command requires declaring that command in the bundle manifest. Loading the module and
withholding the socket achieves the same thing — no endpoint without deliberate action — and
actually works.

If you want the bridge itself up automatically, that is your call to make explicitly: put
`NODALMCPSTART` in your `acaddoc.lsp`. The default will not do it for you.

### What is NOT protected

Be clear-eyed about the residual risk:

- **The model itself is trusted.** If you let a model call `run_csharp`, it can do anything
  your account can do. Tool descriptions state this plainly so the model knows too, and the
  undo classification tells it truthfully which calls are *not* reversible — but there is no
  sandbox, no allowlist and no review step.
- **Anything running as you can read the token file.** The token stops other user accounts
  and browsers, not malware already running under your own account.
- **There is no audit log.** Nothing records which tools ran or what code was executed. If
  you need that for a managed environment, do not deploy this yet.
- **The assemblies are unsigned.** In a CAD-managed firm, trusted paths and plug-in loading
  are often locked down, and an unsigned third-party bundle may simply refuse to load.
- **No timeout on executed code.** A `run_lisp` or `run_csharp` snippet that blocks can wedge
  AutoCAD, and the only way out is the Task Manager.

## Recommendations

- Run it against **scratch drawings**, or a copy, not a live deliverable — especially while
  you are learning what a model does with 133 tools.
- Keep `NODALMCPSTOP` in mind: close the bridge when you are not actively using it.
- Do not run it on a machine that handles drawings you cannot afford to lose or leak.
- Treat it as **alpha**. It is one person's tool, published in case it is useful.

## Reporting a vulnerability

Open a GitHub issue for anything non-sensitive. For something that should not be public
first, use GitHub's private vulnerability reporting on this repository
(Security → Report a vulnerability).

This is a personal project with no SLA. Expect best effort, not a response time.
