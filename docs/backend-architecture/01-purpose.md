# 1. Purpose and how to read this

This document describes how to build a distributed backend on ASP.NET Core using
Domain-Driven Design for the modelling, CQRS for the application layer, and
test-driven development as the working method. It uses an e-commerce domain for
its worked examples because the concepts — orders, stock, payment — are widely
understood, not because the architecture is e-commerce-specific.

## 1.1 Read this first: should you build microservices at all?

Microservices are an organisational solution with a technical implementation.
They exist to let independent teams deploy independently. If you do not have
that problem, you are buying the costs without the benefit.

The costs are real and permanent:

- Every in-process method call that becomes a network call gains partial
  failure, latency, retries, and serialisation.
- Transactions across services do not exist. You get sagas and compensation.
- Debugging requires distributed tracing before it becomes possible at all.
- Local development requires orchestration before anyone can run anything.
- Refactoring a boundary means a coordinated multi-repo, multi-deploy change.

**Default to a modular monolith.** Build the same bounded contexts, the same
aggregates, the same command handlers, the same integration events — but in one
process, with events dispatched in-memory and enforced module boundaries. Every
pattern in sections 5, 6, 7, 8 and 12 of this document applies unchanged. When a
module genuinely needs independent deployment or independent scaling, extract
it: the seams are already cut.

The rest of this document assumes you have made that decision deliberately.

## 1.2 What this document is not

It is not a tutorial, and it does not aim to be complete enough to compile.
Code samples are representative — they show the shape of a pattern, and they
omit null checks, logging and error paths that would obscure the point.

It is also not a menu. The patterns here compose; picking three of them at
random produces something worse than picking none.

## 1.3 Conventions used here

> **Decision** — a choice this blueprint makes, with the reasoning. Recorded in
> full in [Appendix A](appendix-a-adrs.md#appendix-a--architecture-decision-records).

> **Trap** — a mistake common enough that it is worth naming explicitly.

---

[Index](README.md) · [§2 At a glance →](02-architecture-at-a-glance.md)
