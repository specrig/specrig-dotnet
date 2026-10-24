# specrig for .NET

A harness for a .NET service developed specification first: the cases are
written and approved before the code, and the code is changed until they pass.
A service is whatever `Program` builds a host for — a web API, a gRPC service, a
consumer, a worker on a schedule, a command a CronJob runs — and usually several
of them in one host.

**Status: built.** Everything below runs green on two samples — a web API over
Postgres, serving and calling gRPC, and a worker with no server over RabbitMQ and
Quartz. [docs/design.md](docs/design.md) has what each part is built from and why;
what every specrig stack agrees on — the records, the modes, the text of
`specrig.md` — is the [specrig protocol](https://github.com/specrig/protocol), a
copy of which is in [protocol/](protocol).

- **The service runs as it ships.** A case boots the host `Program` builds: the
  real container, a real database in a container. Only the edges are replaced.
- **It is driven the way production drives it.** A request through its own
  pipeline, an event routed by the service's own bindings to its own consumer, a
  job run the way its scheduler runs it, a command with its arguments. A worker that wakes on
  its own runs when a step runs it.
- **Neighbours are stood in for by their contract.** Every origin the service
  calls is named in `tests/internal/Harness.cs`. Every `HttpClient` the container
  builds — typed, named, a gRPC client — reaches a stand-in. A step declares what
  a neighbour answers; the answer and every request the service sends are checked
  against the neighbour's OpenAPI document or proto. A request to an origin the
  harness does not name, a call no step answered, or anything the contract does
  not allow fails the case. What the service publishes is recorded.
- **What a case may replace is granted.** Beyond the stand-ins, a case replaces a
  service of the container only if the harness grants its type; any other
  replacement fails the case, naming the type.
- **Every setting the service reads is answered.** The harness points the
  service's settings at the stand-ins and the resources, and a check fails when
  the code reads a setting that neither the harness nor `appsettings.json`
  answers.
- **Two records in specrig's format.** `tests/surface.yaml` is what the code can
  serve, consume, publish and call, read from the code. `tests/flows.yaml` is
  what the cases make the service send, step by step. A project's flow map reads
  them beside its other services' records — `waffle flows` does.
- **A protocol for the people and agents who write the cases.** `init` writes
  `specrig.md`, which holds an agent to the order: cases, approval, then code, and
  one line in the service's `AGENTS.md` brings it in; `specrig agents` rewrites it.

## Ways in and out

| way in | a step drives it with | named in the records |
|---|---|---|
| an HTTP route | `harness.Http` | `http GET /orders/{id}` |
| a gRPC method | `harness.Grpc<TClient>()` | `grpc /shop.Orders/Get` |
| an event | `harness.Bus.Deliver(routingKey, body)` | `event orders.paid` |
| a job | `harness.Run<TJob>()` | `command SendReminders` |
| a command | `harness.Command("import-prices", args)` | `command import-prices` |

| way out | in a case |
|---|---|
| a neighbour over HTTP or gRPC | a stand-in, checked by the neighbour's contract |
| an event | the bus stand-in, which records it |
| a database, a cache | a resource: the real system, in a container |
| anything else — mail, object storage, a vendor's SDK | a granted seam, or a resource of the service's own |

## Packages

| package | what it is |
|---|---|
| `Specrig` | the core, independent of the host: the records, the contract check, the stand-ins |
| `Specrig.Hosting` | the harness, for any service on the Generic Host: boots `Program`, answers the settings, holds the resources and the grants, the clock and the logs, runs jobs and commands, and puts a test server in Kestrel's place where there is one |
| `Specrig.Grpc` | gRPC both ways: a typed client to the service, and a neighbour stood in for by its service descriptor |
| `Specrig.RabbitMQ` | events in and out over `RabbitMQ.Client`, in process: what the service publishes is recorded, and a delivery is routed by the bindings the service made |
| `Specrig.Quartz` | a Quartz job run as the scheduler runs it, with the detail the service registered |
| `Specrig.Reqnroll` | binds the harness to Reqnroll: the step a record belongs to, the checks after a case, `tests/flows.yaml` after a run |
| `Specrig.Postgres`, `Specrig.Redis` | resources: a real system, provisioned once per run and emptied before every case |
| `Specrig.Surface` | the Roslyn pass behind `tests/surface.yaml` and `tests/checks.yaml` |
| `Specrig.Tool` | the dotnet tool `specrig`: `init`, `surface`, `agents`, and `rules` of the protocol's rules/1 |

## Getting started

In the service's directory:

```bash
dotnet new tool-manifest
```

```bash
dotnet tool install Specrig.Tool
```

```bash
dotnet specrig init --backend billing=Billing:BaseUrl --contract billing=../docs/billing.yaml
```

`init` reads what the service is from its code, through the same Roslyn pass as
`surface`: the settings it reads, the queues it binds, the packages it uses, the
gRPC services it calls. `--backend` names a neighbour the service calls over HTTP
and `--grpc` one it calls over gRPC, each with the setting that holds its address;
a setting the code does not read is refused, with the list of those it does. A
lone gRPC neighbour serves every gRPC service the code calls; with more than one,
`--serves` says which are whose, by package or by service, since a generated
client does not say whose it is. A call no neighbour takes is refused, named. A
connection string whose provider specrig has a resource or a bus for becomes it —
Npgsql a `Postgres`, StackExchange.Redis a `Redis`, RabbitMQ.Client a `RabbitBus`
— and the hosted services that bind a queue go into `Running`. Every other
setting the code reads and `appsettings.json` does not answer becomes a constant
the harness answers empty.

| option | |
|---|---|
| `--backend <name>[=<setting>]` | an HTTP neighbour and the setting that points at it; repeatable |
| `--contract <name>=<path>` | its OpenAPI document, from the service's directory |
| `--failure <name>=<schema>` | the schema of a 5xx it may answer on any route |
| `--grpc <name>[=<setting>]` | a gRPC neighbour and the setting that points at it; repeatable |
| `--serves <name>=<package or service>[,…]` | the gRPC services it serves — `customer`, or `notification.Mail` |
| `--service <name>` | the service's name in its records; the project's name, lowercased, by default |

```bash
dotnet specrig init --grpc ausweis=Ausweis:Address --serves ausweis=customer --grpc herald=Herald:Address --serves herald=notification
```

`init` writes no file that exists, save `specrig.md` and the line an `AGENTS.md`
lacks. It writes:

| file | who changes it |
|---|---|
| `tests/internal/Harness.cs` | whoever owns the platform: a new origin, a resource, a grant, a worker kept running |
| `tests/Specs.csproj`, `tests/reqnroll.json`, the rest of `tests/internal/` | nobody, as a rule |
| `tests/internal/Boot.cs` | the answers an empty service needs to boot, if any |
| `tests/feature/` | the cases |
| `tests/steps/` | the steps |
| `specrig.yaml`, naming the tool the plugin asks | whoever owns the platform: an area, a client's host |
| `AGENTS.md`, and `CLAUDE.md` linked to it; to an `AGENTS.md` there already, the line that brings `specrig.md` in | whoever owns the platform: what the service is for |
| `specrig.md` | nobody: `specrig agents` rewrites it |

and `tests/surface.yaml` and `tests/checks.yaml`, and adds `bin/`, `obj/` and the
test output to `.gitignore`. In a repository whose `Directory.Packages.props`
manages versions centrally it writes the package references bare and names the
versions to add there.

Then:

```bash
dotnet specrig surface
```

```bash
dotnet specrig agents
```

```bash
dotnet test tests
```

The first rewrites `tests/surface.yaml` and `tests/checks.yaml` from the code, the
second `specrig.md` from `specrig.yaml`, and the third runs the suite, as an IDE's
test runner does, and writes `tests/flows.yaml`. How and where they run — a
`Makefile`, a script, CI — is the project's. The `agents` command fails while
`AGENTS.md` does not bring `specrig.md` in, or still carries Rule #11 from before
there was one.

The code runs on the machine, under its debugger; only the resources run in
Docker, started by the suite itself. Without the .NET SDK, and in CI, the same
commands run in the SDK image, given the whole repository the service lives in at
its own path, so a contract or a proto at the repository's root is there, and the
host's Docker socket for the resources:

```bash
docker run --rm -v "$REPO:$REPO" -v /var/run/docker.sock:/var/run/docker.sock \
  -e TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal \
  -w "$PWD" mcr.microsoft.com/dotnet/sdk:10.0 dotnet test tests
```

Without the packages, from a checkout of this repository, `init` run as

```bash
dotnet run --project ../specrig-dotnet/src/Specrig.Tool -- init . --backend billing=Billing:BaseUrl
```

writes a suite that references the checkout's projects, and names the tool in the
checkout in `specrig.yaml`, so `specrig.md` says to run it from there.

## Writing a case

A case is a scenario in `tests/feature/`:

```gherkin
Feature: Reminders

  Scenario: A customer who has not paid is reminded
    Given order 7 is unpaid since yesterday
    And billing holds an invoice of 120 for order 7
    And billing accepts reminders
    When the reminders run
    Then billing is asked to remind the customer of 120 for order 7
```

The suite without steps prints the snippets to start from. Steps receive the
harness:

```csharp
[Binding]
public sealed class Reminders(Harness harness)
{
    [Given("order {int} is unpaid since yesterday")]
    public async Task Unpaid(int order)
    {
        await using var scope = harness.Scope();
        await scope.Get<IOrders>().Save(Order.Unpaid(order, since: DateTimeOffset.UtcNow.AddDays(-1)));
    }

    [Given("billing holds an invoice of {int} for order {int}")]
    public void Invoice(int amount, int order) =>
        harness.Billing.Reply("GET", $"/invoices/{order}", 200, new { amount });

    [Given("billing accepts reminders")]
    public void Accepts() => harness.Billing.Reply("POST", "/reminders", 202);

    [When("the reminders run")]
    public Task Run() => harness.Run<SendReminders>();

    [Then("billing is asked to remind the customer of {int} for order {int}")]
    public void Reminded(int amount, int order)
    {
        var reminder = Assert.Single(harness.Billing.Requests("POST", "/reminders")).Body<ReminderView>();
        Assert.Equal((order, amount), (reminder.Order, reminder.Amount));
    }
}
```

The other ways in are steps of the same shape: `harness.Http.GetAsync(...)`,
`harness.Bus.Deliver("invoices.paid", new { order = 7 })`,
`harness.Command("import-prices")`.

| | |
|---|---|
| `harness.<Backend>.Reply(method, path, status, body)` | what the neighbour answers |
| `harness.<Backend>.ReplyTo(method, path, query, status, body)` | the same, for a request whose query has these values; the reply naming the most of them wins |
| `harness.<Backend>.Hold(method, path, query?)` | the requests the query takes wait until a step releases them; the hold naming the most of the query takes a request |
| `harness.<Backend>.Release(method, path, query?)` | answers what that hold kept waiting with what the neighbour answers by then, and what comes after it at once |
| `harness.<Backend>.Requests(method, path)` | what reached the neighbour, with query and body |
| `harness.<GrpcBackend>.Reply(method, message)` | what a gRPC neighbour answers |
| `harness.<GrpcBackend>.Fail(method, code, reason?)` | the status it refuses with, the reason as the status's `ErrorInfo` |
| `harness.<GrpcBackend>.Requests<T>(method)` | the messages that reached it |
| `harness.Bus.Deliver(routingKey, body, exchange?)` | a delivery, routed by the bindings the service made to the consumers on the queues it reaches; returns how they settled it: acked, requeued or dropped |
| `harness.Bus.Published(routingKey?)` | what the service published, in order: exchange, routing key, headers and body |
| `harness.Scope()` | a scope of the service's container: the repositories a `Given` writes through and a `Then` reads back from |
| `harness.Replace<T>(instance)` | a granted service of the container, replaced before the service boots |
| `harness.Logs` | what the service logged, collected under its real logger |
| `harness.Clock` | the service's `TimeProvider`: it runs with the wall clock until a step sets it |
| `harness.<Resource>.FailWith(error?)` | the resource refuses every client it handed out, until the next case |
| `harness.Bus.FailWith(error?)` | publishing throws what a closed connection throws, or the error given, until the next case |

The service boots at the first step that reaches it, so a `Given` declares and
replaces before anything is built. Each case boots a service of its own: a
singleton holding state does not carry it into the next case. The resources are
provisioned once per run and emptied before every case.

A call no step answered gets a 501, or `Unimplemented` over gRPC, and fails the
case, named. After the case, every request and every answer is checked against the
neighbour's contract: an unknown route or method, an undocumented status, a body of
the wrong shape, or a gRPC answer of another message than the method returns fails
it. So does a request the service sends past the stand-ins — a `new HttpClient()`
of its own — though that request has already left.

A held request does not keep the harness waiting after a step, and one no step
released fails the case, named, unless the service gave up on it. So a case can
give two answers in the order it chooses, or pass a timeout: `harness.Clock`
past it, which that clock counts when the service waits with the `TimeProvider`
it was given.

**`Given` writes through the container's own repositories**, never through the
operation under test, and **`Then` reads back through them**, not out of the
answer: it catches "answered right, persisted wrong".

## `Harness.cs`

```csharp
public sealed class Harness : Specrig.Harness<Program>
{
    public override string Service => "shop";

    public Backend Billing { get; } = new("https://billing.shop.test")
    {
        Setting = "Billing:BaseUrl",
        Contract = "../docs/billing.yaml",
        Failure = "Error",
    };

    public GrpcBackend Customers { get; } = new("https://customers.shop.test", Customer.Descriptor)
    {
        Setting = "Customers:Address",
    };

    public Postgres Database { get; } = new("ConnectionStrings:Shop");

    public RabbitBus Bus { get; } = new("ConnectionStrings:Rabbit");

    protected override Settings Constants => new() { ["Payments:Currency"] = "EUR" };

    protected override Grants Allow => [typeof(IFraudCheck)];

    protected override Workers Running => [typeof(InvoiceEvents), typeof(ReceiptQueue)];
}
```

| member | |
|---|---|
| a `Backend` property | an HTTP neighbour: its origin, the setting that points the service at it, its OpenAPI document, the schema of its 5xx |
| a `GrpcBackend` property | a gRPC neighbour: its origin, the setting, and the descriptors of the services it serves there, which are the contract it answers by. A step names a method by its name, or as `Service.Method` or `package.Service.Method` where two of its services share the name |
| a resource property | a real system, `Postgres` or `Redis`, and the setting that receives its address |
| a `RabbitBus` property | the broker, stood in for in process in place of the `IConnectionFactory` or `IConnection` the container holds, and the setting that holds its connection string, answered with an unroutable address |
| `Constants` | the other settings the harness answers, as given |
| `Allow` | the services of the container a case may replace |
| `Running` | the hosted services that run through every case, as they do in production: every one that binds a queue, and the workers a handler depends on |

A grant is a type, so a renamed interface breaks the build instead of lingering in
a list.

## Hosted services

A host starts its hosted services with it, and in a case only two kinds start:
the server, when the service has one, and those `Running` names. `Running` holds
the consumers — every hosted service that binds a queue, which `init` puts there
and the boot check holds it to, since a consumer left out would leave its events
reaching nothing — and the few workers a handler depends on, such as a queue the
handlers write to and a worker drains. A worker that wakes on its own would send,
at a moment no case chose, what no step answered: a failure with a true message
and no cause in the case. So a step runs the job instead, with
`harness.Run<TJob>()`, and a worker `Running` keeps waits on the harness's clock
where it waits on the `TimeProvider` it was given.

A consumer runs over the bus stand-in's connection, and its exchanges, queues,
bindings and `BasicConsumeAsync` are kept as it asked for them.
`harness.Bus.Deliver` routes a delivery by those bindings, as the exchange
would — a topic pattern with `*` and `#`, a direct key, a fanout — and awaits
the handler of every consumer on the queues it reaches. So a case exercises the
service's own dispatch, and a binding with a typo is a red case. A key no binding
takes throws, naming the bindings there are; a key two exchanges take is
delivered with the exchange named. A delivery the consumer leaves unsettled fails
the case after it, named, unless it consumes with auto-ack.

What the service publishes is recorded and not delivered back to it by itself: a
step delivers it, so the case says that the second half happens.

A command runs `Program` again with its arguments, under the same harness: the
host it builds is caught as the first one was, and its exit code is the step's
result.

A job is resolved from the service's container and run by its `RunAsync`,
`ExecuteAsync`, `Run` or `Execute`. A Quartz `IJob` runs as Quartz runs it: with
the detail the service registered, its data map included, from a scheduler the
service built and nobody started, so nothing it schedules fires by itself.

## Settings

The service reads its settings through `IConfiguration`, and the harness adds its
answers last: they win over `appsettings.json`, user secrets and the environment,
so a developer's own `Billing__BaseUrl` does not send a case to the real billing.
The service runs in the `Production` environment, as it ships, so
`appsettings.Development.json` is not read either, and under its own application
name, not the suite's, so MVC finds its controllers.

The boot is checked against `tests/checks.yaml`, which `specrig surface` writes from
the code: the keys it reads with no fallback — `configuration["…"]`, `GetValue`,
`GetConnectionString` —, those it reads with one, the sections it reads whole, and
the hosted services that bind a queue. A key read with no fallback and answered by
neither `appsettings.json` nor the harness fails the boot, named; so does a key the
harness answers that the code reads in none of those ways, and a consumer `Running`
leaves out.

A resource takes its own address from its own chain — the address given, then
`<SERVICE>_TEST_POSTGRES` and the like, then a container — so a developer's
database is welcome where the service's settings are not.

## Postgres

```csharp
public Postgres Database { get; } = new("ConnectionStrings:Shop") { Keep = ["currencies"] };
```

The address is the one given as `Address`, else `<SERVICE>_TEST_POSTGRES`, else a
container Testcontainers starts. The container is kept between runs — starting
postgres costs seconds, a database a fraction of one — and the database is the
run's own: `shop_test_<pid>`, created at the first boot and dropped when the run
ends, so two test projects running at once never share one. A run killed before
its end leaves its database behind, and the next run drops it: a name of that
shape, marked by specrig over an hour ago, with nobody connected. An address that
leads nowhere fails the boot, naming its host and port and never the whole
address, which may carry a password.

The schema is the service's own: at the first boot of the run every EF Core
`DbContext` the container registers — or makes through an `IDbContextFactory` — is
migrated, or has its model's tables created where it has no migrations. Before
every case Respawn empties every table it finds, but `__EFMigrationsHistory` and
those `Keep` names.

The service reaches the database through a gate of the case's own, on loopback.
`harness.Database.FailWith(null)` closes it: what connects is refused and what is
connected is dropped, whatever client the service talks through, and an EF Core
context refuses its connection at once — an `NpgsqlException` over a refused
socket, which the service meets as it would meet a database that is down, wrapped
by Npgsql's execution strategy included. Another exception fails every EF Core
context with that one. A fault a step sets before the boot waits for it, so the
migration goes through; the next case boots whole.

## Redis

```csharp
public Redis Cache { get; } = new("ConnectionStrings:Cache");
```

The address is `Address`, else `<SERVICE>_TEST_REDIS`, else a container of the
run's own — starting redis costs less than a second, so nothing is kept — removed
when the run ends. Before every case `FLUSHDB` empties it. `FailWith(null)` makes
every command of the `IConnectionMultiplexer` the container holds throw a
`RedisConnectionException`, the database a step took before the fault included.

## The records

Both are specrig's format and say so first — `specrig: surface/1`,
`specrig: flows/1` — so that a project's flow map finds the service by them and
reads them beside the others; [protocol/records.md](protocol/records.md) is what
every writer agrees on. Each is rewritten by what has its source, and nothing compares it on the
way: a project that rewrites them in CI and then runs `git diff --exit-code` sees a
record that fell behind as a diff.

**`tests/surface.yaml`** lists what the code can serve, consume, publish and
call; `dotnet specrig surface` writes it. It is read from the
service's project through Roslyn:

| | recognised at |
|---|---|
| serves | `MapGet`, `MapPost`, `MapPut`, `MapPatch`, `MapDelete` and `MapMethods` under their `MapGroup` prefixes; a controller action by its `[Route]` and `[Http*]` attributes, those of the methods it overrides and of its base classes too, as an NSwag-generated controller declares them, `[controller]` and `[action]` filled in; `MapGrpcService<T>()`, by the generated service `T` derives from |
| consumes | `QueueBindAsync`, by the routing key it binds; a key bound with a wildcard is written as bound — `orders.*` |
| publishes | `BasicPublishAsync`, by the routing key it names |
| calls | a request through an `HttpClient` — `GetAsync`, `PostAsync`, `PutAsync`, `PatchAsync`, `DeleteAsync`, `SendAsync` and the `FromJson`/`AsJson` extensions; a Refit interface's attributes; a generated gRPC client's methods |
| clients | the HTTP requests above, by the typed client that sends them |

A name is followed through constants, interpolated strings, concatenation and the
callers of the method that builds it, and one that is not a constant at the end
of that chain is refused: it is a contract, and a contract has a name. A route
keeps its parameters as written, without constraints — `{id:int}` is `{id}` — and
a catch-all is `*`; a varying segment of a request is `{}`, and a request whose
whole address comes from data is `GET external`. A gRPC method is
`/<package>.<Service>/<Method>`.

What a record cannot learn from the code — the neighbour a client calls, when the
project cannot tell — goes into `specrig.yaml` beside it, with the tool `init`
names there for the plugin:

```yaml
guard: [dotnet, specrig]
flows:
  clients:
    Shop.Billing: billing
```

**`tests/flows.yaml`** lists what the cases make the service send. Whatever a
`When` step makes reach the service is an input — a request by the route it
matched, a gRPC call by its method, an event by its routing key, a job or a
command by its own name — and what left the service after it, until the next
`When` or `Given`, is what it sent: a request named by the route of the
neighbour's contract that takes it, an event by its routing key. A refusal is named by its status and what the
service says it is: `HTTP 404 order_not_found` from a problem's `type`,
`NotFound order_not_found` from a gRPC status's `ErrorInfo` reason. Before the
next step the harness waits for what the service has yet to send: the calls in
flight to it and from it, then 50 ms with nothing more.

Every whole run rewrites it, a failing one included; a run narrowed by `--filter`
writes nothing, which the harness tells by the scenarios that ran against the
scenarios in `tests/feature/`. Only the `description` fields are written by hand:
they survive a rewrite, and one whose step is gone moves to `orphaned`, comes back
with its step, and fails the run meanwhile.

## The protocol

The `specrig.md` that `init` writes holds the people and agents who write the cases
to three steps, with a stop after each: the cases, then approval by a human, then
the code, changed until every case that was red for its own reason is green.
Every edit is made in a mode only the human switches — spec, redgreen or
platform, and redgreen until one is switched — and an edit the mode does not allow
is a stop. Auto mode, which the human switches on too and nothing offers, runs the
whole cycle without a stop for a human who reviews it all at once.

| mode | may be edited |
|---|---|
| spec | `tests/feature/**/*.feature` |
| redgreen | the steps and production code — anything but the cases and the platform's files |
| platform | the platform's files, and production code for a seam |
| auto | `tests/feature/**/*.feature`, the steps and production code — anything but the platform's files |

The platform's files are `tests/internal/`, `tests/Specs.csproj`,
`tests/reqnroll.json`, `.config/dotnet-tools.json`, `specrig.yaml`, `AGENTS.md` and
`CLAUDE.md`; `specrig.md`, `tests/surface.yaml` and `tests/checks.yaml` are
generated and open in no mode. An **area** is a named set of paths, opened in its
own modes or granted by the human on top of one:

| area | paths | open in | granted in |
|---|---|---|---|
| `csproj` | `tests/Specs.csproj` | platform | redgreen — a package a step needs is not a platform change |
| `flows` | `tests/flows.yaml` | every mode — the run writes it, a description is written by hand | — |
| `steps` | `tests/steps/` | as the table says | platform — steps that follow a harness change |

`specrig.yaml` declares the service's own under `agents.areas`, and replaces a
default by naming it; a monorepo's shared contract is one:

```yaml
agents:
  areas:
    contract:
      paths: [../../shared/api/]
      modes: [spec, platform]
```

`rules` prints what the tables of `specrig.md` are rendered from — the modes, the
layout and every area's paths — for the service in the working directory, as one
line of JSON, rules/1 of the protocol:

```
dotnet specrig rules
```

The tool is in the service's own manifest, so the rules move with the service's
version of specrig. In Claude Code the specrig plugin enforces them:

```
/plugin marketplace add specrig/specrig
/plugin install specrig@specrig
```

`/specrig:spec ./path/to/service` switches, `/specrig:allow csproj` grants, and the
plugin reads the rules of the tool `specrig.yaml` names once a session and refuses
an edit the mode does not allow before it happens. It runs on the
machine Claude Code runs on, so it needs the .NET SDK there; without it the edits
are let through, and the plugin says so once.

## Limits

- A service on the Generic Host: `Host.CreateApplicationBuilder`,
  `WebApplication.CreateBuilder` or `Host.CreateDefaultBuilder`. A command whose
  `Program` builds no host is not driven. Before .NET 10, a `Program` of
  top-level statements needs `public partial class Program;`.
- `Program` hands its `args` to the builder: the answers reach the service on its
  command line, and a boot whose configuration does not read them fails, naming
  the setting.
- A neighbour is stood in for only where the container builds the `HttpClient`.
  A `new HttpClient()` reaches no stand-in; the harness sees its request and fails
  the case, but the request has already left. A request on a thread that carries
  no case's mark is laid to every case alive; the suite runs its cases one at a
  time.
- A gRPC neighbour answers unary calls; a stream to it is not stood in for.
- `RabbitMQ.Client` 7.x is the bus with an adapter, and its connection has to
  come from the container. A hosted service that consumes through another client
  is a worker the harness cannot drive. The stand-in covers declaring, binding,
  publishing, consuming and settling; any other member of `IChannel` throws,
  naming it, and so does a headers exchange.
- Quartz is the scheduler with an adapter; any other job runs by its `RunAsync`,
  `ExecuteAsync`, `Run` or `Execute`.
- `Postgres` and `Redis` are the resources there are. Anything else is a resource
  of the service's own, an `IResource`: `Provision`, `Configure`, `Migrate`,
  `Reset`, every one called in every case, and once per run the resource's own to
  keep.
- The database is migrated through EF Core alone; another migration tool is not
  run. A fault other than a refusal reaches EF Core's contexts alone.
- `tests/surface.yaml` reads what the table above names: a request through a
  library it does not, MassTransit or Kafka say, is not in it.
- A description that lost its anchor fails the run with an assembly cleanup
  failure; why, the test logger says only at `detailed` verbosity.

## Development

```bash
make generate
```

```bash
make test
```

The first rewrites the two samples' `tests/surface.yaml`, `tests/checks.yaml` and
`specrig.md` through the tool. The second builds every project and runs the tests: the packages'
own, the protocol's record, marker and agents fixtures in `protocol/fixtures/`, `surface`
and `init` over the gateway in `test/fixtures/gateway/` — NSwag-shaped controllers,
neighbours over gRPC alone —, then the suites of the two samples in `samples/` —
`shop`, a web API over Postgres serving and calling gRPC, and `reminders`, a worker
over RabbitMQ with a Quartz job and a command — which rewrite their
`tests/flows.yaml`. The resources start in Docker. `make generate-docker` and
`make test-docker` run the same in the SDK image, given the host's Docker socket,
for a machine without the SDK.

```bash
docker rm -f $(docker ps -aq --filter label=specrig=postgres)
```

removes the kept Postgres container.

```bash
make protocol
```

refreshes `protocol/` from a checkout of the protocol beside this one, `PROTOCOL` to
name another; the tests then run against the new copy.

Every package and the tool carry the one version in `src/Directory.Build.props`. A
tag `v<version>` matching it runs the tests and publishes the packages to NuGet
through `.github/workflows/release.yml`, which nuget.org trusts for this repository:
no API key is kept, and the secret `NUGET_USER` names the nuget.org account that
owns the packages.
