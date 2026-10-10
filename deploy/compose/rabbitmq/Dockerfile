# The broker, plus the one plugin §9.6's saga cannot run without.
#
# MassTransit's `UseDelayedMessageScheduler` (ADR-021) schedules a message by
# publishing it to a delayed exchange, and on RabbitMQ that exchange type comes
# from `rabbitmq_delayed_message_exchange` — a community plugin that ships in
# no official image.
#
# Without it, measured: the bus starts CLEAN, and the first `.Schedule(…)`
# fails `exchange.declare` with `precondition_failed: unknown exchange type
# 'x-delayed-message'`, after which MassTransit retries the topology for ever.
# The call never returns, the saga's transition never completes, and the only
# trace is a channel error in the BROKER's log every few seconds — the service
# stays healthy and silent. That is §9.6's stuck order arriving as
# infrastructure rather than as a missing transition.
#
# The tag is pinned to a minor rather than tracking `4`, and that is the
# plugin's constraint rather than a preference: the plugin is built against a
# specific broker line and `rabbitmq-plugins enable` refuses one it does not
# match. A floating `4` would enable cleanly today and fail the image build on
# whatever Tuesday RabbitMQ 4.2 becomes latest.
FROM rabbitmq:4.1-management-alpine

# ADD rather than curl, so the fetch is a build step Docker caches and a
# reviewer can read — not a runtime `rabbitmq-plugins enable` against a volume
# nobody looks at.
ADD https://github.com/rabbitmq/rabbitmq-delayed-message-exchange/releases/download/v4.1.0/rabbitmq_delayed_message_exchange-4.1.0.ez \
    /opt/rabbitmq/plugins/

# The digest and the mode, in a RUN rather than as `ADD --checksum= --chmod=`.
# **Both of those flags are BuildKit-only**, and this image is built by two
# different builders: Compose uses BuildKit, and §12.4's Testcontainers fixture
# uses the classic `/build` API, which rejects them outright — "the --chmod
# option requires BuildKit". A Dockerfile only one of them can build is a
# fixture that silently falls back to a stock broker, which is the exact defect
# this file exists to prevent.
#
# The checksum still fails the build on a substituted asset; what changes is
# that it fails one layer later, after the file has been written rather than
# before. Nothing reaches a running broker either way.
#
# The chmod is not tidiness and the failure it prevents is worth naming: ADD
# from a URL writes the file 0600 and root-owned, the broker runs as
# `rabbitmq`, and the plugin is then unreadable. `rabbitmq-plugins enable
# --offline` never opens the archive — it edits enabled_plugins — so the IMAGE
# BUILDS CLEANLY and the container dies at start with an Erlang `eacces` naming
# a path inside the .ez. Measured exactly that way here.
#
# --offline because the broker is not running during a build; the change lands
# in enabled_plugins and takes effect at start.
RUN cd /opt/rabbitmq/plugins \
    && echo "567f876378e70af9d949de4066bbb2fc30162b46bcbc9efe2430076b172e4a87  rabbitmq_delayed_message_exchange-4.1.0.ez" \
        | sha256sum -c - \
    && chmod 644 rabbitmq_delayed_message_exchange-4.1.0.ez \
    && rabbitmq-plugins enable --offline rabbitmq_delayed_message_exchange

# The shovel, and it is a RUNBOOK dependency rather than a platform one. Nothing
# this solution runs needs it — §13.9's `error-queue.md` does, because the only
# non-destructive way to move poison messages back to their endpoint is a
# one-shot shovel, and a procedure that cannot be executed on the image the
# repository ships is not a procedure.
#
# Both plugins ship IN the base image, unlike the delayed exchange above, so
# this needs no download and no checksum — only enabling. `_management` is the
# half that exposes /api/parameters/shovel, which is what the runbook drives.
RUN rabbitmq-plugins enable --offline rabbitmq_shovel rabbitmq_shovel_management

# The exporter §13.6's three queue alerts read. Nothing this solution runs needs
# it to start; `rabbitmq_queue_messages` is published by this plugin or by
# nothing, so without it `ErrorQueueDepth`, `SkippedQueueDepth` and
# `QueueBacklogGrowing` load and can never fire (#800). Shipped in the base
# image like the shovel above, so only enabling.
#
# PER-OBJECT, because the plugin's default /metrics is AGGREGATED:
# `rabbitmq_queue_messages` there is one number for the whole node with no
# `queue` label, and `queue=~".+_error"` matches nothing in it. Per-object
# output gives the series its label under the name the rules and the runbooks
# already use. The alternative, /metrics/detailed?family=queue_coarse_metrics,
# is the plugin's route above a few hundred queues but publishes
# `rabbitmq_detailed_queue_messages`, so every rule, runbook and
# EXTERNAL_METRICS would be renamed. A local stack's queue count is far below
# the cost the plugin warns about.
#
# A RUN rather than a COPY, and not for tidiness: the permissions gate holds
# every COPY here to a mapping in §12.4's fixture, which runs the STOCK image,
# and a stock broker has no plugin schema for this key. Nothing the fixture
# tests reads the series.
RUN rabbitmq-plugins enable --offline rabbitmq_prometheus \
    && echo "prometheus.return_per_object_metrics = true" > /etc/rabbitmq/conf.d/30-prometheus.conf

# §9.4's per-service identity (#44). Two files, and both are plain COPY for the
# reason the checksum above is a RUN: this image is built by BuildKit under
# Compose and by the classic /build API under §12.4's Testcontainers fixture,
# and anything BuildKit-only makes the fixture fall back to a stock broker.
#
# THE PERMISSIONS ARE A MEASUREMENT, NOT A GUESS, and definitions.json is JSON
# so it cannot say so itself. They were read off a running broker with both
# services connected — `rabbitmqctl list_exchanges/list_queues/list_bindings`
# after an order had been placed, so the saga's scheduled timeout had declared
# its exchange too. What that found:
#
#   * a receive endpoint is a queue `X` plus a fanout exchange `X`, bound;
#   * a consumed message type is a fanout exchange named
#     `Common.Contracts.<Context>.V1:<Type>`, bound to the endpoint exchange;
#   * MassTransit's delayed scheduler (ADR-021) declares
#     `ordering-fulfilment-saga_delay`, type `x-delayed-message` — named after
#     the RECEIVE ENDPOINT, which is the fact that makes an `ordering-` prefix
#     sufficient. A pattern that missed it would not fail loudly: the Dockerfile
#     comment above records that a broken delayed exchange retries the topology
#     for ever while the service stays healthy and silent.
#
# The permissions follow RabbitMQ's own verbs rather than an intuition about
# ownership: `configure` is declare/delete, `write` is publish AND being a
# binding's DESTINATION, `read` is consume AND being a binding's SOURCE. That
# split is why Ordering needs `read` on Catalog's contract exchanges — it binds
# them to its own queues — and why it does not need `write` on them. A consumer
# that could write its producers' exchanges could forge their events.
#
# So the property this buys is exactly #44's: `catalog-svc` has no `write`
# matching `ordering-commands` or `Common.Contracts.Ordering.V1:*`, so a
# compromised Catalog cannot send `ConfirmOrder` and cannot publish an
# `OrderPlaced`. §9.4's `CommandOrigin.System` stamp — "arriving on this
# service's command queue is what earns it" — is enforced by the broker from
# here rather than merely asserted by the mapper.
#
# WHAT IT DOES NOT BUY IS `configure` EXCLUSIVITY, and that is structural. A
# consumer declares the exchange it binds, so Ordering's `configure` has to
# reach `Common.Contracts.*` — including Catalog's. Ordering can therefore
# delete an exchange it does not own. Closing that needs a topology where
# producers pre-declare and consumers only bind, which MassTransit does not
# offer; it is stated in §9.4 rather than left to be discovered.
#
# ONE VHOST, DELIBERATELY. A vhost per service is the obvious next step and is
# wrong here: a vhost is a full namespace, so two services in different vhosts
# share no exchange and cannot exchange messages at all. Vhosts separate
# tenants; these users separate services inside one.
COPY definitions.json /etc/rabbitmq/definitions.json
COPY 20-commerce.conf /etc/rabbitmq/conf.d/20-commerce.conf
