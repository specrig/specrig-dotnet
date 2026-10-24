# agents/1

What every stack writes so that an agent meets the same Rule #11 whichever service
it is in: `specrig.md`, written whole from these parts and the stack's own, and one
line in the service's `AGENTS.md` that brings it in.

| file | whose | written |
|---|---|---|
| `specrig.md` | the stack's tool | whole, by `init` and by the command the `writer` slot names, which the service runs with whatever else it generates. [modes.yaml](modes.yaml) puts it in `generated`, so no mode opens it, and an edit by hand is lost on the next run |
| `AGENTS.md` | the service's | by `init` as a stub where there is none, and by `init` adding [the reference](#the-reference) to one that lacks it. No tool writes it after that |
| `CLAUDE.md` | the service's | by `init` as a link to `AGENTS.md` where there is none, so Claude Code reads the same file |

`CONTRIBUTING.md` is the humans' and overrides `specrig.md`, Rule #11 included: no
tool writes it.

`specrig.md` is written from what can be read at any time — the service's name where
the stack's records take it from, `specrig.yaml`, the stack's layout — and never from
what `init` was told. It names no neighbour: a neighbour the platform owner adds
after `init` would leave a file that listed them wrong, and what a case may stand in
for is said by the file the `cut` slot names, which the text sends the agent to
first.

## specrig.md

| part | where in the file |
|---|---|
| [agents/header.md](agents/header.md) | first |
| [agents/contributing.md](agents/contributing.md) | after it |
| [agents/short.md](agents/short.md) | under `## The short version` |
| [agents/rule11.md](agents/rule11.md) | after it, as written |

What comes after Rule #11 — how the suite runs, a framework's own conventions — is
the stack's.

## The reference

[agents/reference.md](agents/reference.md) is the line that brings `specrig.md` into
`AGENTS.md`: Claude Code reads `@specrig.md` as an import and loads the file, and
another agent reads the line as an instruction. `init` puts it under the title of the
stub it writes, and at the end of an `AGENTS.md` that lacks it; where it stands after
that is the service's choice. What else a stub holds is the stack's.

The command the `writer` slot names checks `AGENTS.md` on every run and fails on any
verdict but `ok`; `init` adds the reference where it is missing and says what else is
left. The first verdict that holds decides:

| verdict | when |
|---|---|
| `absent` | there is no `AGENTS.md` |
| `stale` | a line outside code reads `## Rule #11 — specification before code`, trailing whitespace aside: the file carries the text `specrig.md` holds now, from before there was one |
| `unreferenced` | no `@specrig.md` outside code stands alone — after the start of its line or whitespace, before the end of its line or whitespace |
| `ok` | otherwise |

Outside code is outside fenced blocks and inline code spans, where Claude Code
imports nothing. A fenced block opens at a line that starts, after up to three
spaces, with three or more backticks or tildes, and closes at the next such line of
the same character and at least as long; one never closed runs to the end of the
file. A code span is a run of backticks and what follows it on the line up to the
next run of the same length; a run with none after it is text. A reference followed
by punctuation does not stand alone: `@specrig.md.` names another file.
[fixtures/agents](fixtures/agents) pins the cases.

## Slots

A slot is `{{name}}`, replaced by the stack's text as it is. A slot alone on its
line and filled with nothing takes the line with it, and a blank line left doubled
becomes one. A slot the stack does not fill, or a value for a slot the text does
not have, fails the stack's own test.

| slot | what | may be empty |
|---|---|---|
| `what` | what the repository holds: `service`, `app` | |
| `writer` | the stack's command that writes `specrig.md`: `npx --no specrig agents` | |
| `version` | the tool that writes it, with its version: `@specrig/web 0.4.0` | |
| `cut` | what a case may stand in for, read before the cycle — waffle's permission list, specrig-dotnet's `tests/internal/Harness.cs` | |
| `validate` | what runs after every edit | |
| `boundary` | why the platform boundary holds with the rule off | |
| `spec-paths` | the paths of the person writing the specification | |
| `platform-paths` | the paths of the person who owns the platform | |
| `tasks` | rows a stack adds to "Which task this is" | yes |
| `modes` | the rows of the mode table, from [modes.yaml](modes.yaml) | |
| `platform-files` | the platform's files, as a list | |
| `generated` | a sentence on what the stack's tool and the run write | yes |
| `tool` | whose the default areas are: `waffle`, `specrig` | |
| `config` | the marker file: `waffle.yaml`, `specrig.yaml` | |
| `areas` | the rows of the area table | |
| `cases` | what the executable specification is | |
| `contracts` | a paragraph on how the contracts a case leans on are checked, and where they are listed | yes |
| `describes` | what a case is written about, and what a neighbour answers | yes |
| `binding` | how a case is bound in red-green: the harness, what may be stood in for, when the cut does not fit | |

A mode row is `| <name> | <allows> |`, with `{{cases}}` in `allows` filled by the
cases' patterns. An area row is `| <name> | <paths> | <open in> | <granted in> |`:
paths in backticks, `as the table says` for an area without `modes`, `no mode` for
`modes: []`, `—` for no grant.
