# The records

What specrig's records hold and how each writer names what it saw. Each stack has
its writer — waffle for a Go service, specrig-js for a web app, specrig-dotnet for a
.NET service — and `flows` (today `waffle flows`) reads them all as one system,
joining services by the names in them. A name one writer spells differently from another is a link lost,
so this page is what they agree on.

Each record says first what it is, as an OpenAPI document does under `openapi:`, and
a reader refuses a record of a version newer than it reads.

## tests/surface.yaml — `specrig: surface/1`

What the code can serve, consume, publish and call, read from the code.

```yaml
specrig: surface/1
service: shop
publishes:
  - reminders.sent
consumes:
  - invoices.paid
serves:
  - /shop.Orders/Get
  - GET /orders/{id}
calls:
  - /customers.Customers/OfOrder
  - GET /invoices/{}
clients:
  Shop.Billing:
    - GET /invoices/{}
```

| key | what | named |
|---|---|---|
| `publishes` | events the service publishes | the routing key, or the topic |
| `consumes` | events it takes | the routing key it binds, as bound: a wildcard stays one |
| `serves` | HTTP routes and gRPC methods it answers | `GET /orders/{id}` with the parameter's own name and no constraint, a catch-all `*`; `/<package>.<Service>/<Method>` |
| `calls` | HTTP requests and gRPC methods it sends | `GET /invoices/{}` with every varying segment `{}`; `GET external` when no part of the path is constant |
| `clients` | the HTTP requests above, by the client that sends them | keyed by where the client lives in the code |
| `domains` | in a modular monolith, calls between its own domains | `rent → invoice: Invoices.Issue` |

Every list is sorted by bytes and holds each entry once; a key with nothing under it
is left out.

## tests/flows.yaml — `specrig: flows/1`

What the cases make the service send, written from the trace of a whole run.

```yaml
specrig: flows/1
service: shop
inputs:
  - message: http GET /orders/{id}
    description: written by hand, kept across rewrites
    fields:
      - page[]
    needs:
      http POST /payments:
        - card.id
    branches:
      - sends:
          - http GET /invoices/{orderId}
        sent:
          http GET /invoices/{orderId}:
            - order
        cases:
          - "orders.feature: The customer sees what is left to pay"
      - refused: HTTP 404 order_not_found
        cases:
          - "orders.feature: An order the shop does not have is refused"
outputs:
  - message: http GET /invoices/{orderId}
orphaned:
  - anchor: http GET /gone
    description: a description whose input is gone
```

A **message** is the kind and the name: `http GET /orders/{id}`, `grpc /shop.Orders/Get`,
`event invoices.paid`, `command SendReminders`.

| kind | in, named | out, named |
|---|---|---|
| `http` | by the route the request matched | by the route of the neighbour's contract that takes it, else as sent |
| `grpc` | `/<package>.<Service>/<Method>` | the same |
| `event` | the routing key delivered | the routing key published |
| `command` | a command's verb; a job's class, without its namespace | — |
| `ui` | a web app's `When` step, as written | — |

An **input** is what reached the service while a `When` step acted; what a `Given`
sets up and a `Then` reads is not one. What left the service after an input, until
the next input, is what it **sends**.

A **branch** is the cases that, having received the input, sent the same messages in
the same order, or were refused the same way. A block sent again straight after
itself is one pass of a loop, so the number of items a case had does not split its
branch. A **refusal** is named by its status and what the service says it is:
`HTTP 404 order_not_found` from the body's `type` — not a URI — and
`NotFound order_not_found` from gRPC's code, as Go's `codes.String()` spells it
(`Canceled`), and the lowercased reason of the status's `ErrorInfo`. An event a
consumer did not acknowledge is `requeued` or `dropped`. A job or a command that
throws is named by the short name of what it threw — `RuntimeException` — and a
command that exits otherwise than with 0 by `exit <code>`.

**Fields** are leaf paths, arrays folded into `[]`: `items[].sku`. A null or an empty
container carries none. An input's `fields` are those of every case of it; a branch's
`sent` are those of what it sent, by message; an output's `fields` are those of every
case that sent it. An input's `needs` names, by message it sends, the fields carried
in every case of the answered branches sending the message and in no case of the
others: anything weaker is the fixtures' habits.

Only the `description` fields are written by hand. They are kept by **anchor** — the
input's message, or `<message> -> <sends, comma-separated>` with ` (refused <refusal>)`
after it for a branch — and one whose anchor is gone moves to `orphaned`, comes back
with its anchor, and fails the run meanwhile.

A run narrowed to some cases writes nothing: it saw part of the service. Every whole
run rewrites the record, a failing one included.

Inputs are sorted by message, branches by anchor, cases, fields, outputs and the keys
of `needs` and `sent` by bytes. Keys in a mapping keep the order above.

## tests/checks.yaml — `specrig: checks/1`

What a service's boot is checked against, read from its code beside
`surface.yaml`. `flows` does not read it.

```yaml
specrig: checks/1
service: reminders
settings:
  - Billing:BaseUrl
  - ConnectionStrings:Rabbit
optional:
  - Reminders:Limit
sections:
  - Billing
consumers:
  - Reminders.InvoicesPaid
```

`settings` are read with no fallback, `optional` with one, `sections` whole;
`consumers` are what binds a queue and so has to run in a case. Each stack names
them as its code reads them:

| stack | settings, optional | sections | consumers |
|---|---|---|---|
| .NET | configuration keys: `Billing:BaseUrl` | the sections read whole | the hosted services |
| PHP | environment variables: `BILLING_URL` | none | the console commands |

Laravel's `env()` always falls back, to null if to nothing else, so a Laravel
service has only `optional`; Symfony's `%env(X)%` without a `default:` processor and
`#[Autowire(env: 'X')]` are `settings`.

## specrig.yaml

Beside the service, what neither the records nor the tools can learn from the
code. A waffle service keeps the same keys in `waffle.yaml`.

```yaml
guard: [dotnet, specrig]
flows:
  clients:
    Shop.Billing: billing
  prefixes:
    - transport/http.router.prefix
agents:
  areas:
    events:
      paths: [../../shared/api/events/]
      modes: [spec, platform]
```

| key | what |
|---|---|
| `guard` | the service's tool, as its arguments, which prints the service's [rules](guard.md#rules1); it marks the directory as a service |
| `flows.clients` | the host of a client the set cannot tell |
| `flows.prefixes` | the group prefixes configuration sets |
| `agents.areas` | the service's own [areas](guard.md#areas), and replacements for the defaults its tool ships |

A `flows` key that is not needed fails the check rather than lingers.

## The trace

The fixtures under `fixtures/records/` hold runs as a writer traces them — the
input to the record — so a writer in any language can run the same files.

```json
[
  {
    "case": "orders.feature: An order the shop does not have is refused",
    "records": [
      { "seq": 1, "dir": "in", "kind": "http", "name": "GET /orders/{id}", "step": "Action", "error": "HTTP 404 order_not_found" }
    ]
  }
]
```

| field | what |
|---|---|
| `case` | `<feature file>: <scenario title>` |
| `seq` | the order the records were made in, across the case |
| `dir` | `in` for what reached the service, `out` for what it sent |
| `kind`, `name` | the message, as the table above names it |
| `step` | what the step that was acting is for: `Context` for a `Given`, `Action` for a `When`, `Outcome` for a `Then` |
| `request` | the body, decoded; its leaf paths are the fields |
| `error` | the refusal, named as above |

`<name>.trace.json` is a whole run, and `<name>.flows.yaml` the record it comes out
as, byte for byte.
