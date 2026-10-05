# Backend Architecture Blueprint

**A reference architecture for ASP.NET Core microservices using DDD, CQRS and TDD.**

| | |
|---|---|
| **Status** | Reference blueprint — adapt, don't copy wholesale. The C# solution it specifies is built in this repository ([Appendix C](appendix-c-delivery-plan.md)) |
| **Target runtime** | .NET 10 (LTS), C# 14 |
| **Last reviewed** | 2026-08-02 |
| **Availability figures** | Illustrative. The arithmetic of compounding is the point; the inputs are round numbers, not measurements |
| **Sample domain** | E-commerce, as a reference implementation: the worked example an adopter replaces, while the building blocks, gates, scaffold and operating contract are kept ([ADR-062](adr/ADR-062-the-domain-is-a-reference-implementation.md)) |
| **Revision** | Revised across successive design reviews. Load-bearing corrections: transaction failure and rollback semantics, outbox type and payload stability, single message identity, business counters as claims rather than calls, public URL shape. Originally merged with a parallel design, which contributed the delivery plan, composition-root gate, Redis eviction isolation, dual DB identities, cursor pagination and hop budget |

---

## Chapters

| | |
|---|---|
| **1** | [Purpose and how to read this](01-purpose.md) |
| **2** | [Architecture at a glance](02-architecture-at-a-glance.md) |
| **3** | [Bounded contexts and service decomposition](03-bounded-contexts.md) |
| **4** | [Solution and folder structure](04-solution-structure.md) |
| **5** | [Tactical DDD](05-tactical-ddd.md) |
| **6** | [CQRS](06-cqrs.md) |
| **7** | [Persistence](07-persistence.md) |
| **8** | [Caching with Redis](08-caching-redis.md) |
| **9** | [Messaging](09-messaging.md) |
| **10** | [API Gateway](10-api-gateway.md) |
| **11** | [Identity and authorization](11-identity-authorization.md) |
| **12** | [Test strategy and TDD](12-test-strategy.md) |
| **13** | [Observability](13-observability.md) |
| **14** | [Local development](14-local-development.md) |
| **15** | [CI/CD and deployment](15-cicd-deployment.md) |
| **App. A** | [Architecture decision records](appendix-a-adrs.md) |
| **App. B** | [Dependency licence register](appendix-b-licences.md) |
| **App. C** | [Delivery plan](appendix-c-delivery-plan.md) |
| **App. D** | [Names the solution does not declare](appendix-d-type-inventory.md) |
