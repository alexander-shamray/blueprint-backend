# blueprint-backend

A reference architecture for ASP.NET Core microservices using DDD, CQRS and TDD.

**[→ Read the blueprint](docs/backend-architecture/README.md)** ·
[Delivery roadmap](docs/roadmap.md)

| | |
|---|---|
| **Status** | Reference blueprint — adapt, don't copy wholesale. The C# solution it specifies will be built in this repository ([delivery plan](docs/backend-architecture/appendix-c-delivery-plan.md)) |
| **Target runtime** | .NET 10 (LTS), C# 14 |
| **Sample domain** | E-commerce (illustrative only) |

Covers bounded contexts and service decomposition, solution structure, tactical DDD,
CQRS, persistence, Redis caching, messaging and the outbox pattern, API gateway,
identity and authorization, test strategy, observability, local development, and
CI/CD — plus appendices with architecture decision records, a dependency licence
register, a delivery plan and the names the solution does not declare.
