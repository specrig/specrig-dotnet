# specrig protocol

What every specrig stack agrees on, written down once. A stack —
[specrig-go](https://github.com/specrig/specrig-go),
[specrig-js](https://github.com/specrig/specrig-js),
[specrig-php](https://github.com/specrig/specrig-php),
[specrig-dotnet](https://github.com/specrig/specrig-dotnet) — has its own harness,
because how a service is booted and stood in for is its stack's; what crosses the
stacks is here, with fixtures each implementation runs to show it agrees.

| part | version | what |
|---|---|---|
| [records.md](records.md) | `surface/1`, `flows/1`, `checks/1` | the records a service's suite writes, and `specrig.yaml` beside them; what `specrig-flows` joins services by |
| [system.md](system.md) | `index/1` | how the records of every service are read as one system, and the index of its flows that comes out |
| [guard.md](guard.md) | `guard/1`, `rules/1` | whether the mode on allows editing a path: the rules a stack's tool prints, the verdict a plugin draws from them, and the marker that names the tool |
| [modes.yaml](modes.yaml) | `modes/1` | the Rule #11 modes as data: the classes of a service's paths and which mode opens which |
| [specrig-md.md](specrig-md.md) | `agents/1` | `specrig.md`, which every stack writes whole — Rule #11 and the short version, with slots for the stack — and the line that brings it into the service's `AGENTS.md` |
| [why.md](why.md) | | why Rule #11 is written as it is: what lasts, what compensates, and the reasons kept out of `specrig.md` |
| [fixtures/records](fixtures/records) | | runs as a writer traces them, and the records they must come out as |
| [fixtures/flows](fixtures/flows) | | sets of services' records, and the index each must come out as; `specrig-flows` runs them |
| [fixtures/guard](fixtures/guard) | | layouts and areas, and the verdict on each path in each mode; the plugin runs them |
| [fixtures/markers](fixtures/markers) | | `agents.areas` as a marker may hold them, and which a stack's `rules` takes and which it refuses |
| [fixtures/agents](fixtures/agents) | | `AGENTS.md` as a service may hold it, and the verdict of the check that `specrig.md` is brought in |

Nothing here runs. The tools that work across stacks are written in Go: the Claude
Code plugin that enforces the modes is [specrig](https://github.com/specrig/specrig),
and `specrig-flows`, which reads every service's records as one system, is released
with [specrig-go](https://github.com/specrig/specrig-go) for each platform, so a
project on any stack runs it without Go.

## Taking it

Every stack takes a copy pinned to a tag of this repository, refreshes it itself,
and runs the fixtures in its own tests. Its published package carries the copy, so
nothing is fetched from here once it is installed.

A version is the name a file carries — `surface/1`, `guard-fixtures/1`. A change a
reader of the old version would misread is a new number; an implementation says
which numbers it implements and refuses a newer one.
