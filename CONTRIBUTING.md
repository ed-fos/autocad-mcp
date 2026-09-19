# Contributing

Thanks for looking. Setting expectations honestly up front, because this is a one-person
project and I would rather be clear than polite:

- **This is not a staffed project.** I maintain it alongside commercial work. Issues and PRs
  may sit for a while. That is not disinterest, it is capacity.
- **Bug reports are genuinely useful**, especially "this tool produced the wrong geometry" or
  "this crashed AutoCAD", with the drawing state and the exact call.
- **Large unsolicited PRs are risky** for both of us. Open an issue first so we can agree the
  shape before you spend the time.

## Ground rules for changes

### Security-sensitive code

`HttpRequestGuard`, `HttpBridge` and the escape hatches in `EscapeTools` / `CSharpBridge` /
`LispBridge` sit in front of arbitrary code execution. For anything touching those:

- The tests in `tests/Nodal.AcadMcp.Tests/HttpRequestGuardTests.cs` are the contract. If a
  change makes one fail, the change is wrong until proven otherwise.
- Do not weaken the token, `Host`, `Origin` or `Content-Type` checks for convenience. Read
  [SECURITY.md](SECURITY.md) first — each one is there for a specific, named attack.
- New tools that execute caller-supplied code must declare `UndoKind.ToolDefined` or
  `UndoKind.NotUndoable`. Advertising something as a single Ctrl+Z when it is not is the kind
  of bug that costs someone a drawing.

### AutoCAD API rules

- **Everything touches AutoCAD on the main thread.** Go through `Acad.Read` / `Acad.Write`,
  which marshal via `Application.Idle`. Calling the AutoCAD API from a worker thread is an
  immediate hard crash, not a race you will notice in testing.
- `Acad.Write` locks the document and commits once, which is what makes a tool one undo step.
  If you cannot use it — as `run_lisp` and `send_command` cannot, because LISP and commands
  drive their own transactions — say so in the tool's undo classification.
- Dispose what you own, and nothing AutoCAD owns.

### Describing tools

Tool descriptions are the API documentation, because they are what the model actually reads.
Name every argument in `camelCase`, give units and defaults, and state which arguments are
mutually exclusive. The input schema is deliberately permissive; the description carries the
contract.

### ACA API notes

Several ACA modules carry long comments recording what the declared API surface does and does
not support — which creation sequences are verified, which members are ambiguous, and why
certain features are not implemented. Keep those current. They are the most valuable
documentation in the repository, and they exist because this API is thinly documented.

## Building

```bash
dotnet build Nodal.AcadMcp.slnx -c Release
dotnet test tests/Nodal.AcadMcp.Tests
powershell -ExecutionPolicy Bypass -File deploy.ps1
```

The add-in compiles without AutoCAD installed — the core API comes from NuGet — but the ACA
tools and `run_csharp` are compiled out, because those references are probed from the AutoCAD
install directory. So CI can syntax-check the non-ACA half and nothing more; anything touching
the 70 ACA tools has to be built and tested on a machine with AutoCAD Architecture 2027.

## Licence

Contributions are accepted under [Apache-2.0](LICENSE), the same licence as the project.
