# Design

What specrig for .NET is built from, where it follows its two siblings and where
it cannot, what the owner decided, and the order it was built in.

## Where it sits

| | for | how it reaches the service | records |
|---|---|---|---|
| specrig-js | a web app | a browser, Playwright | the app's requests to its backends |
| waffle | a Go service built with waffle | a harness generated from the service's DI container | inputs over gRPC, HTTP, events and commands, and what the service sends on |
| specrig-dotnet | a .NET service on the Generic Host | the host `Program` builds, caught as it is built | the waffle kind, plus the outbound HTTP waffle does not trace |

It is the waffle half without waffle: a standalone harness, like specrig-js, for a
service whose layout it does not own. waffle can lean on knowing its own
libraries and generating its own container; here the container is Microsoft's,
built at run time, and the libraries are the service's choice. So what waffle
reads off generated code, this reads off the running container or the code
through Roslyn, and what waffle knows about gorib, this has to declare as a
supported set and a list of limits.

## A host, not a server

waffle is not an HTTP framework with extras: an application is a list of
commands, and HTTP is one of them. The .NET shape that matches is the Generic
Host, not ASP.NET Core — ASP.NET Core is one hosted service in it, beside the
consumers and workers, and a worker service has no server at all.
`WebApplicationFactory` is therefore not the harness's base, only the model for
it: the harness catches the host `Program` builds through the hosting
diagnostic events, the way `WebApplicationFactory` and `dotnet ef` do, and puts a
test server in Kestrel's place only where there is one.

| waffle | daemon | in a .NET service | in a case |
|---|---|---|---|
| `http` | yes | Kestrel, under `GenericWebHostService` | a test server in its place; `harness.Http` |
| `grpc` | yes | Grpc.AspNetCore on the same server | a channel over the test server; `harness.Grpc<T>()` |
| `scribus` | yes | a consumer: a hosted service that binds queues on a `RabbitMQ.Client` channel | runs over the bus stand-in's connection; `harness.Bus.Deliver` |
| `outbox` | yes | a relay: a hosted service | stopped unless `Running` names it; the outbox row is what a `Then` reads |
| `cron` | no — the scheduler | Quartz, Hangfire, a `PeriodicTimer` loop, a Kubernetes CronJob | stopped; a step runs the job |
| `migrate`, a domain command | no | a job, or a verb `Program` takes | a step runs it: `harness.Run<T>()`, `harness.Command(...)` |

## The format comes first

`waffle flows` joins services by the names in their records, so a .NET record that
names a call differently from its Go neighbour is a link lost without an error
anyone reads as one. The names, as the two existing writers produce them:

| what | name | Go writes it from | .NET writes it from |
|---|---|---|---|
| an HTTP route served | `GET /orders/{id}` | the mux pattern | `RouteEndpoint.RoutePattern`: a leading `/`, constraints and defaults dropped, a catch-all `*` |
| an HTTP request sent | `GET /invoices/{}` in `surface`, `GET /invoices/{orderId}` in `flows` | the path; not traced | the path through Roslyn; the neighbour's contract template, as specrig-js names it |
| a request with no constant path | `GET external` | the client call | the same |
| a gRPC method | `/order.OrderService/Place` | the full method | the request's path, the generated client's descriptor |
| an event | `contracts.updated` | the routing key of `Publish` and `QueueBind` | the routing key of `BasicPublishAsync` and `QueueBindAsync` — see [the first bus](#the-first-bus-rabbitmqclient) |
| a command | `media:sweep-otp` | `Command.Name()` | a job's name in its scheduler, else its type's; a command's verb |
| an HTTP refusal | `HTTP 404 order_not_found` | status and the body's `type` | status and `ProblemDetails.Type`, dropped when it is a URI: ASP.NET's defaults are links to the RFC |
| a gRPC refusal | `NotFound order_not_found` | code and the failure's type, or `ErrorInfo.Reason` of domain `gorib_app_error_v1`, lowercased | `StatusCode` and `ErrorInfo.Reason` from the rich status, lowercased |
| a record's message | `http GET /orders/{id}` | `kind + " " + name` | the same |

The YAML itself has to match byte for byte too — key order, indentation, quoting,
the sort by bytes — because CI compares a rewritten record with `git diff`.

The format lived in two codebases and in waffle's `conventions/flows.md`. A third
writer was the moment to write it down, and it now lives in the
[specrig protocol](https://github.com/specrig/protocol) — the records, the modes,
the rules and the text of `specrig.md` — with fixtures: a trace in, the record out,
which specrig-dotnet runs from its copy in `protocol/`; a path and a mode in, the
verdict out, which the plugin runs against the rules every stack prints.

## The pieces and what they are built on

| piece | built on |
|---|---|
| boot | the hosting library's diagnostic events, as `HostFactoryResolver` uses them — its sources package is not on nuget.org: `Program` runs on a thread of its own, and `HostBuilding` is where the stand-ins and the grants go in; environment `Production`, the application named after the service's assembly as `WebApplicationFactory` names it, one host per case |
| the server | a `TestServer` registered as `IServer`, where the host has a server |
| hosted services | removed but for the server and those `Running` names, the consumers among them |
| a job | resolved from the container and run by its `RunAsync`, `ExecuteAsync`, `Run` or `Execute`; a Quartz `IJob` through `Specrig.Quartz`, with the detail the service registered, from a scheduler nobody started; the adapter is found by name beside the suite, so a suite need not name a type of it |
| a command | `Program` run again with the verb and its arguments; the host it builds caught like the first |
| stand-ins | an `IHttpMessageHandlerBuilderFilter` sets one primary handler under every client the container builds, after the client's own configuration — typed, named and `Grpc.Net.ClientFactory` alike; it routes by origin to an `IStandIn`, HTTP or gRPC |
| a request past them | the `HttpHandlerDiagnosticListener`: the stand-in replaced the handler that reports, so every request it reports went around. It is laid to the case whose mark the thread carries — set on the host's thread, an inbound request and a job — else to every case alive |
| an HTTP contract | the OpenAPI document parsed from YAML; schemas validated by NJsonSchema with `components.schemas` rewritten to `definitions` |
| a gRPC contract | the descriptors of the services one origin serves: frames decoded with the method's parser, an answer encoded, the status in the trailers `Grpc.Net.Client` reads |
| the bus | an adapter per bus: an in-process stand-in for the broker that records what is published, keeps the wiring the consumers ask for, and delivers through their handlers; `RabbitMQ.Client` first, [below](#the-first-bus-rabbitmqclient) |
| inbound HTTP | a middleware first in the pipeline names the call by the endpoint it matched |
| inbound gRPC | the same middleware, by the request's path: the body copied as the server reads it, so a stream is not waited on, decoded by the request type's own parser; the refusal from the trailers, the reason from the rich status's `ErrorInfo` |
| settings | `Program`'s command line, which the default builders read last, so the answers win over `appsettings.json` and the environment even for what `Program` reads before it builds the host; the keys the code reads come from the same Roslyn pass as `surface`, written to `tests/checks.yaml` |
| resources | `IResource`: `Provision` before the boot answers its setting, `Configure` sees the container, `Migrate` and `Reset` follow the start; `Rig.Ending` takes what comes down when the run ends — the Reqnroll hook, or the process exit for any other runner |
| Postgres | Testcontainers, a container kept between runs with Testcontainers' reuse; a database per process with a mark and a sweep; every EF Core `DbContext`, registered or made by a factory, migrated, or its tables created; Respawn over the tables found after migrating |
| Redis | Testcontainers, a container of the run's own, removed when it ends; `FLUSHDB` before every case |
| a resource that goes down | for Postgres a loopback gate of the case's own, closed for a refusal, so every client fails, beside a connection and a command interceptor on every EF Core context for an error of the step's choosing; for Redis a proxy over the container's `IConnectionMultiplexer` and every database and batch it hands out |
| logs | `AddFakeLogging`: a provider beside the real ones, so a case reads what the production logger built |
| clock | `TimeProvider` replaced by one that follows the wall clock until a step sets it, a `FakeTimeProvider` from then |
| steps | Reqnroll: `[BeforeStep]` and its `StepDefinitionType` mark Given, When and Then; the hooks live in `Specrig.Reqnroll`, named in `reqnroll.json` |
| `tests/flows.yaml` | `[AfterTestRun]`; a run is whole when the scenarios that ran are every scenario of `tests/feature/`, read by the English keywords, `@ignore` left out |
| `tests/surface.yaml` | Roslyn over the service's project, through `MSBuildWorkspace`, restored first; generated files, under `obj/` or named `.g.cs`, are read for their types — an NSwag controller's routes among them — and not walked for calls |
| rules | rules/1 of the protocol: its `modes.yaml` embedded in the tool, the layout `init` lays out as classes, default areas and the service's own from `specrig.yaml`; `specrig.md` is the protocol's text with this stack's slots, rendered from the same table and from nothing `init` was told, and the service's own `AGENTS.md` brings it in with one line |
| distribution | NuGet, every package at one version, published by `.github/workflows/release.yml` for a tag `v<version>`; the tool in the service's local manifest, or run from a checkout |

## The first bus: RabbitMQ.Client

An event is named by its routing key, as the Go services name it, and the
exchange is not part of the name: `event orders.paid` is published by whoever
publishes that key and consumed by whoever binds it. That is what lets
`waffle flows` link a .NET consumer to a Go publisher, and back, with no
declaration on either side.

| | |
|---|---|
| version | 7.x: `IConnection`, `IChannel` and the async API. 6.x's `IModel` is an adapter of its own, written when a service needs it |
| the seam | the `IConnectionFactory` or `IConnection` the container holds, replaced by the stand-in. The connection string is answered with an unroutable address, so a connection built past the container fails the boot, naming the setting |
| the consumers | the hosted services that bind queues, in `Running`: `init` puts there those the Roslyn pass finds, and the boot check fails on one that binds a queue and is not there — its events would reach nothing |
| wiring | `ExchangeDeclareAsync`, `QueueDeclareAsync`, `QueueBindAsync` and `BasicConsumeAsync` are kept as the service asked for them |
| publishing | `BasicPublishAsync` is recorded with its exchange, routing key, headers and body, and confirmed at once; after `FailWith` it throws what a closed connection throws |
| a delivery | `harness.Bus.Deliver(routingKey, body)` routes as the exchange would — a topic pattern with `*` and `#`, a direct key, a fanout — to the consumers on the queues it reaches, and awaits each handler. A headers exchange is refused, named |
| settling | `BasicAckAsync` settles; `BasicNackAsync` and `BasicRejectAsync` requeue or drop, as asked, and `Deliver` returns which. A delivery the consumer leaves unsettled fails the case after it, named, unless it consumes with auto-ack |
| a key no binding takes | `Deliver` throws, naming the key and the bindings there are |
| one key on two exchanges | `Deliver` asks which: `Deliver(routingKey, body, exchange: "billing")` |
| the service's own events | not delivered back by themselves: a step delivers what the service published, so the case says that the second half happens |
| the rest of `IChannel` | throws `NotSupportedException` naming the member, so a gap in the stand-in fails loudly instead of doing nothing |
| `surface` | `publishes` at `BasicPublishAsync`'s routing key, `consumes` at `QueueBindAsync`'s, followed through constants, wrappers and their callers as the Go pass follows `Publish` and `QueueBind`; a key that is not a constant at the end is refused. A key bound with a wildcard is written as bound — `orders.*` — and the flow map reports what a wildcard alone takes, as it does for Go |

Routing by the bindings is stricter than waffle, whose consumer recorder hands
every delivery to the handler and keeps the bindings only to be asserted on.
Here a binding with a typo is a red case, and nothing has to remember to assert
on it.

MassTransit, Kafka and the rest are adapters of their own, later, held to the
same rule for the name. A MassTransit service links to a Go one only where it
binds by routing key, which is not MassTransit's default topology; and
MassTransit is commercial from v9.

## Decisions taken, and why

**The Generic Host is the base.** A harness built on `WebApplicationFactory`
would make every worker and consumer a second-class way in, which is the shape
waffle refuses. `HostFactoryResolver` is the same mechanism without the server.

**Workers run when a step runs them.** In waffle a cron command runs when a step
runs it, and nothing ticks in a component test. A .NET worker started with the
host would wake at a moment no case chose and send what no step answered — a
failure with a true message and no cause in the case. So the host starts without
its workers, and `Running` names the few a handler depends on, which then wait on
the harness's clock.

**The bus is stood in for in process.** As waffle's recorders: what is published
is recorded, and a delivery goes through the consumer the container installed. A
broker would add delivery timing that no case asserts on, and the semantics a
broker adds between services — retries, ordering — are what the component cut
removes by design, as waffle's `ai-native` says.

**A container kept, a database per run.** waffle measured it: starting postgres
takes about 2.6 s, creating a database and migrating it about 0.2 s. So the
container outlives the run and the database does not, and the process id in its
name is what lets two test projects run at once against one container — `dotnet
test` over a solution runs them in parallel. A run killed before its end is
cleaned up by the next one's sweep, as in waffle: a name of the shape, specrig's
mark over an hour old, nobody connected.

**A case's fault is the case's.** `FailWith` lives on the case's own resource
object, the gate and the interceptors are the case's own, so the next case boots
whole with nothing to heal. A migration runs through the service's contexts too,
and is let through.

**A service per case.** A singleton that keeps state — a cache, an in-memory
queue — is the leak between cases nobody sees until the order changes. The
expensive parts are the containers, which are kept, and the database, which is
emptied; building the host is measured before anybody trades isolation for it.

**`Production`.** The pipeline that ships, without the developer exception page,
and without `appsettings.Development.json`, which points at somebody's machine.

**The harness's settings win.** In waffle the developer's environment wins over
the `env` hook; here it loses, as it does in specrig-js, because the setting that
matters most is a neighbour's address and a developer's own one must not send a
case to a real neighbour. A resource reads its own variable, and there a
developer's database is welcome.

**Stand-ins under the client, not in place of it.** waffle cuts an outbound gRPC
dependency at the typed client, because below it is
`Invoke(ctx, method string, args, reply any)`: a stand-in there would check the
wire's language instead of the behaviour. Under a .NET client the stand-in has
the neighbour's contract — an OpenAPI document, a service descriptor — so it
checks the request against what the neighbour accepts and answers with typed
messages. That is what specrig-js does, and it is what lets the record name what
the service sent.

**Grants are types.** A grant in `Harness.cs` is `typeof(IFraudCheck)`, checked by
the compiler, in a platform file either way. A YAML list would hold a renamed
interface until somebody noticed.

**A recorder, not a mock.** `Then` follows `When` by grammar, and a mock declares
before the call — waffle's reasoning, unchanged. The stand-ins, the bus, the logs
and the clock are recorders; a case that wants a mock brings its own for a
granted type.

**`surface` reads the code, not the running service.** The endpoint table of a
booted service is exact, but booting needs the database, and `specrig surface`
should not. The other two writers read code as well. The design-time build runs
`Grpc.Tools`, so the generated clients are in the compilation for their types.

## The owner's calls

What was open is decided, and stays the owner's to reopen:

1. **The schedulers.** Quartz has an adapter; a `PeriodicTimer` loop, a Hangfire
   method or a verb of `Program` a CronJob runs needs none.
2. **Target framework.** `net10.0` alone, the LTS of the day.
3. **The test framework under Reqnroll.** xUnit v3.
4. **Where the format is written down.** In the specrig protocol, a repository of
   its own, with fixtures each stack runs from a copy pinned to it.
5. **Enforcement in Claude Code.** One plugin for every stack,
   [specrig](https://github.com/specrig/specrig): it reads the rules of the tool a
   service's `specrig.yaml` or `waffle.yaml` names and checks every edit itself, so
   no stack has a plugin or a guard of its own.

## Order, and where it stands

| phase | delivers | done when | stands |
|---|---|---|---|
| 0 | `specrig/1` written down, with fixtures | the Go and JS writers pass them | in the protocol; the .NET writer passes the record fixtures, and the plugin's guard the guard fixtures |
| 1 | the host boot, the settings, HTTP stand-ins and the OpenAPI check, the checks after a case, the Reqnroll binding, `Postgres`, logs, the clock; HTTP, jobs and commands as ways in; `tests/flows.yaml`, `init`, `specrig.md` | two samples are green — a web API and a worker with no server that runs a job — and `waffle flows` reads their records beside a Go service's | built; `waffle flows` reads both samples' records, so far without a Go service beside them |
| 2 | `tests/surface.yaml` through Roslyn — serves, calls, clients — and the settings check | the samples' links show in `waffle flows` with no `system` failure | built; `system` fails only on the parties the two samples lack — a customers service, a caller of `shop.Orders/Get`, a publisher of `invoices.paid`, a consumer of `reminders.sent` |
| 3 | `RabbitMQ.Client` both ways: the stand-in, `Deliver`, `publishes` and `consumes` | an event a Go service publishes reaches the .NET consumer in the flow map, and one the .NET service publishes reaches a Go consumer | built; not yet tried beside a Go service |
| 4 | gRPC both ways, grants, `FailWith`, `Redis` | a gRPC neighbour is stood in for by its descriptor | built |
| 5 | `rules` and enforcement | an edit outside the mode is refused in Claude Code | rules/1 built; the specrig plugin enforces it with the machine's own `dotnet` — tried here through the SDK image standing in for one |

What phases 1 and 2 stand at is what `waffle flows`, built from waffle's sources,
says of `samples/`. Billing is named in `samples/*/specrig.yaml`, as the format
asks for a host the set cannot tell.
