# guard/1

Rule #11 works in modes, and only the human switches them. Whether the mode on
allows editing a path — and if not, which mode or grant would — is decided by [the
verdict](#the-verdict) from a service's **rules**: the [table](modes.yaml), the
classes the stack's layout fills, and the service's areas. The stack's tool prints
them; a plugin — what an agent's edits go through — checks every path against them.
The tool keeps no state and switches nothing: which mode is on and what was granted
is the plugin's to track. So one plugin enforces every stack, and the rules a
service is held to come from the tool version it depends on.

## rules/1

```
<tool> rules
```

`<tool>` is what the service's [marker](#the-marker) names, and it runs in the
service's directory. It prints one JSON object, on the last line of its output that
starts with `{`, so what a launcher prints first does not count:

```json
{"specrig":"rules/1","modes":[…],"classes":[…],"areas":[…]}
```

| key | what |
|---|---|
| `modes` | the modes of [modes.yaml](modes.yaml), in its order and with its keys; `{{cases}}` in `allows` filled with the stack's patterns for the cases |
| `classes` | the classes of modes.yaml, in its order: each `name`, and its `patterns` — the protocol's, then the layout's |
| `areas` | the service's [areas](#areas), the stack's defaults with the marker's merged in: each `name` and `paths`, and `modes` and `grant` where set |

Patterns are relative to the service, as everywhere here. A marker the tool does not
accept — see [Areas](#areas) — is `Error: <what>` and exit 1, and a plugin refuses
the service's edits with it until the marker holds, save the marker itself in
platform mode, where it is fixed. Rules of a version a plugin does not read are a tool that did
not run: the plugin says so, and leaves the service's edits unchecked.

## Before rules

A tool older than `rules` answers `Unknown command "rules"` and exits non-zero, and a
plugin asks it the commands rules/1 replaced instead:

```
<tool> guard <mode> <path> [--grant <area>]...
<tool> mode <mode>
<tool> grant <mode> [<area>]
```

| command | answers |
|---|---|
| `guard` | exit 0 and nothing printed: the edit is allowed. Exit 0 and a line starting `unclaimed:`: the path is outside the service and no area of it names it — not this service's to decide. Exit 1 and `Error: <reason>`: refused, the reason naming every way to the path |
| `mode` | `Mode: <name>`, the mode's brief, `May be edited: <allows>.` — the text a plugin shows the agent when the human switches |
| `grant` | with no area, the areas the mode opens on their own and those it takes as a grant; with one, `Grant: <area>, on top of <mode> mode, until the mode changes.` and `May also be edited: <paths>.`, or `Error: <why not>` |

`<path>` is absolute, or relative to the service. A mode, an area or an argument the
tool does not know is `Error: <what>` and exit 1 — save a `--grant` to `guard` naming
no area of this service, which opens nothing and is no error: a plugin passes the
session's grants to every service the task names. A tool older than these too
answers `Unknown command "<command>"`, and a plugin takes that for a service with no
modes yet and lets its edits through.

## The verdict

A path is checked in this order, and the first rule that speaks decides:

1. **`CONTRIBUTING.md`** at the service's root: refused in every mode, with no way
   to it. It is the humans'.
2. **Generated** — `specrig.md`, or a path of the layout's `generated` class:
   refused in every mode, with no way to it. The stack's tool writes it.
3. **An area** that claims the path, the most specific one:
   - granted, in a mode the area takes grants in: allowed;
   - with `modes`: allowed in exactly those; the ways are those modes and the
     area's grants;
   - without `modes`, inside the service: the class table decides, and the area's
     grants add ways to it;
   - without `modes`, outside the service: only a grant opens it.
4. **Inside the service**, no area: the path's class, and the modes that open it.
5. **Outside**, no area: `unclaimed`.

A mode that `includes` others in [modes.yaml](modes.yaml) — `auto` includes `spec`
and `redgreen` — is each of them to an area: an area open in one is open in it, and
a grant taken in one is taken in it.

A refusal names its **ways**: every mode but the one on that would allow the path
and that modes.yaml does not mark `offered: false` — `auto` is never named — then
`the <area> grant in <mode> mode` for each grant that would, the mode on named for a
grant it takes through one it includes, and a grant in a mode marked
`offered: false` named only while that mode is on; each way is named once. With no
way at all it says no mode opens the path. The wording is the plugin's own; the ways are the
protocol, and the [fixtures](fixtures/guard) pin them:
`<mode> mode does not allow <path>; <ways, joined by ", or "> does — stop and ask the human for it`.

A path inside the service is shown relative to it, one outside as written.

## The layout

Each stack's tool fills the classes of [modes.yaml](modes.yaml) with patterns for the
layout it lays out; the protocol fixes the classes' order, what each mode opens, and
the two files every stack shares.

| class | open in |
|---|---|
| `humans` — `CONTRIBUTING.md` | no mode |
| `generated` — `specrig.md`, and what else the stack's tool writes | no mode |
| `platform` | platform |
| `cases` | spec, auto |
| `tests` — the rest of the suite | redgreen, auto |
| `production` — everything else in the service | redgreen, platform, auto |

A path takes the first class that names it, in that order.

## Areas

An area is a named set of paths. `grant` lists the modes in which the human may open
it by name, until the mode changes; `modes`, when set, takes its paths out of the
class table and opens them in exactly those modes — `modes: []` closes them in all.
An area without `modes` follows the table, so its `grant` only adds.

A stack's tool ships default areas — among them `steps`, the suite's steps, which
Rule #11 names — and the service's marker file declares its own under
`agents.areas` and replaces a default by naming it:

```yaml
agents:
  areas:
    events:
      paths: [../../shared/api/events/]
      modes: [spec, platform]
    deps:
      paths: [package.json, package-lock.json]
      modes: [platform]
      grant: [redgreen]
```

An area is a mapping; `paths` is a list of strings, not empty, and `modes` and
`grant`, where given, lists of strings naming modes the table has. Anything else —
a string where a list goes, a mapping, a null — is `Error:` from `rules`, naming the
area, and the [fixtures](fixtures/markers) pin which markers a tool takes.

## Patterns

Paths of a class or an area are relative to the service, and may leave it.

- `/` separates names; a pattern ending in `/` claims the directory and everything
  below it.
- A name may hold `*`, `?` and `[…]`, matching within that name.
- `**` as a whole name matches any number of names, none included.

Where areas overlap, the one with more names before its first wildcard decides —
`api/proto/` inside an `api/` area is the first's — then the one with more names
after it, then the name that sorts first.

A path is compared as it is on disk, each name spelled as its directory lists it, so
on a case-insensitive filesystem `Go.mod` is `go.mod`. A path through a symlink is
checked where it is written and where it leads, and refused if either is.

## The marker

A plugin finds the service of a path by walking up from it to the nearest directory
holding a marker, and asks the tool the marker names:

| marker | tool | asked |
|---|---|---|
| `specrig.yaml` with `guard` | its `guard`, as arguments: `[dotnet, specrig]`, `[npx, --no, specrig]`, `[go, tool, specrig]` | `rules` |

A `specrig.yaml` without `guard` marks a service with no modes yet. The marker names
the service's own tool, never one fetched: `npx` without `--no` would install
whatever the registry holds under that name where the service's packages are not
installed, and run it.

## What a plugin does with it

The rules are the protocol; holding the mode is the plugin's. The specrig plugin for
Claude Code, [specrig](https://github.com/specrig/specrig), is one: it reads a
service's `rules` once a session and again when its marker changes; the human
switches with `/specrig:<mode>` and grants with `/specrig:allow <area>`, one mode
holds for every service the task names, a service the task does not name is closed
whole, every service is in redgreen until the human switches a mode — and a switch
to redgreen says whether it is the approval, which it is only from spec mode — and
an edit the rules refuse is refused before it happens, with the reason.
Without a plugin the agent reads the same tables in `specrig.md` and holds the
boundary itself.
