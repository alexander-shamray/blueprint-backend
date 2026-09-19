{{- /*
The name every object a chart renders takes — and the one thing here that is
deliberately NOT derived from the release name.

Helm's convention is `{{ .Release.Name }}-{{ .Chart.Name }}`, and it is wrong
for this platform: these names are ROUTING CONFIGURATION. The gateway's route
configuration (§10.2, `Gateway.Api/appsettings.json`) dials each workload by
this literal name, and so does the BFF's one synchronous hop, `PricingHop.cs`'s
— both on the record that the host does not vary because it is the Kubernetes
Service name. A release-derived name makes that false the moment the umbrella
chart installs the same workload under a different release, and the failure is
a 502 rather than a template error.

So the name is a value, and it is required. `workload.name` is what the Service
is called, which is what a peer dials.
*/}}
{{/*
`required` is not enough on its own, and every guard in these charts rested on
it until Copilot said so.

Helm's `required` fails on nil and on the empty string, and passes anything
else — including `" "`. The hosts do not agree: `AddJwtAuthentication` guards
with `IsNullOrWhiteSpace` and says in its own comment that an environment
variable set to the empty string arrives as `""` rather than null. So an
overlay with `identity.authority: " "` rendered cleanly, began a rollout, and
died in the new pod — which is the failure every render-time guard here exists
to move earlier.

BLANK COUNTS AS MISSING is already a lesson in docs/lessons.md, learned twice against
this exact key. This is the third time, and it goes in one helper so there is
one place to be wrong. `toString` before `trim` because `trim` errors on a
non-string, and `tag: 1.2` is a YAML float.
*/}}
{{- define "commerce.require" -}}
{{- $value := index . 0 -}}
{{- $message := index . 1 -}}
{{- required $message ($value | default "" | toString | trim) -}}
{{- end -}}

{{- define "commerce.name" -}}
{{- include "commerce.require" (list .Values.workload.name "workload.name is required: it is this deployable's Service name, and therefore the string the gateway's route file and the BFF's pricing hop dial (§10.2, §9.7).") -}}
{{- end -}}

{{- /*
The image tag, required rather than defaulted.

values.yaml carries `tag: ""` deliberately (§15.3): a deploy that cannot name
its image must fail rather than roll something nobody chose. CI supplies it
from the build; a config-only deploy reads the running value back out of the
cluster first (§15.1). `required` is what turns the empty default into a
refusal — without it the empty string renders `image: registry/api:` and the
kubelet resolves that to `:latest`, which is the one tag §15.3 forbids by name.
*/}}
{{- define "commerce.tag" -}}
{{- $tag := include "commerce.require" (list .Values.image.tag "image.tag is required and values.yaml leaves it empty on purpose: a deploy that cannot name its image must fail rather than roll something nobody chose (§15.3). CI supplies it; a config-only deploy resolves the running tag first (§15.1).") -}}
{{- /*
The tag is not only an image reference here: it goes into the migration Job's
NAME and into `app.kubernetes.io/version`, and the three have different
alphabets. `Release_1` is a perfectly valid OCI tag, an invalid DNS-1123
subdomain (uppercase), and therefore a Job the API server refuses — after
`helm upgrade` has started. The render gate exercised commit SHAs and never
saw it.

So the accepted shape is the intersection, and the binding constraint is the Job
name: a DNS-1123 **subdomain** is dot-separated labels, each of lowercase
alphanumerics and dashes, each starting and ending alphanumeric. Underscores
are out entirely — legal in a label VALUE and not in a name — and so are empty
or hyphen-bounded segments.

A single regex over the whole string is what got this wrong the first time:
`^[a-z0-9]([a-z0-9._-]*[a-z0-9])?$` admits `release_1`, `release..1` and
`release.-1`, all of which Kubernetes refuses after `helm upgrade` has started.
The segments have to be checked as segments.

Validating is preferred to sanitising: a derived metadata value that differs
from the image tag makes `app.kubernetes.io/version` a label naming something
no registry has.
*/}}
{{- range $segment := splitList "." $tag }}
{{- if not (regexMatch "^[a-z0-9]([a-z0-9-]*[a-z0-9])?$" $segment) }}
{{- fail (printf "image.tag %q is not usable as Kubernetes metadata: the segment %q is not a DNS-1123 label. The tag becomes the migration Job's name and app.kubernetes.io/version, so each dot-separated segment must be lowercase alphanumerics and dashes, starting and ending alphanumeric — which every commit SHA and ordinary semver already is (§15.3)." $tag $segment) }}
{{- end }}
{{- end }}
{{- /*
And the length, because `app.kubernetes.io/version` carries the tag on every
chart — including the two with no migration Job, where the Job-name budget
never applies. A label value may not exceed 63 characters, and this used to be
handled by truncating, which produced a version label naming a tag no registry
has.
*/}}
{{- if gt (len $tag) 63 }}
{{- fail (printf "image.tag is %d characters. It becomes app.kubernetes.io/version, and a label value may not exceed 63 (§15.3)." (len $tag)) }}
{{- end }}
{{- $tag -}}
{{- end -}}

{{- /*
The selector, which carries the workload name and NOTHING release-derived.

**Because the selector is workload identity, not release bookkeeping.** These
pods are found by their name: the Service selects them, and that name is the
string §10.2's route file and §9.7's pricing hop dial. Putting the release into
the selector would make a pod's identity depend on which command installed it,
for a field a Deployment will never let you change afterwards.

This comment used to justify it by the standalone-to-umbrella migration, and
that justification is dead — §15.3 and `platform/values.yaml` now record that
Helm rejects the adoption outright on ownership, so the migration never reaches
the API server's immutable-selector check. The conclusion survives its original
argument, which is worth saying rather than quietly keeping: a release-scoped
selector would still be wrong, and the reason is now the one above.
*/}}
{{- define "commerce.selectorLabels" -}}
app.kubernetes.io/name: {{ include "commerce.name" . }}
app.kubernetes.io/part-of: commerce
{{- end -}}

{{- /*
§15.5's canary, and the three helpers below are the whole of it in this chart.

**The canary is a SECOND RELEASE of the same chart**, differing in
`canary.enabled`, its replica count and its image tag. The stable release is
never touched, which is what makes a rollback cost the canary's own pods and
nothing else — no `helm rollback`, and no image change on the pods serving the
rest (ADR-022).

**The schema is not part of that, and this comment used to say it was.** The
canary release runs §7.4's migration hook — it is the first thing carrying the
new image — so a rollback removes the pods and leaves the schema migrated.
ADR-022 says so in its own consequences; what makes it survivable is §15.5's
requirement that every migration be backward compatible with the previous
release, which the cheap rollback does not buy and does not excuse.

Traffic splits because BOTH tracks answer to the SAME Service: the selector
above carries the workload name and nothing about the track, so kube-proxy
spreads connections across every pod behind it and the share the new version
serves is `canary / (stable + canary)`. That is also why the weight is
quantised — `deploy/canary/canary.py` does that arithmetic and refuses the
weights this cannot express.
*/}}
{{- define "commerce.track" -}}
{{- if .Values.canary.enabled }}canary{{ else }}stable{{ end -}}
{{- end -}}

{{- /*
The name of every object THIS RELEASE owns, which is not the workload's name.

Helm stamps `meta.helm.sh/release-name` on what it creates and refuses to touch
another release's objects (§15.3, platform/values.yaml). So the canary release
cannot render a `catalog-api` Deployment or ConfigMap — those belong to the
stable release, and the install fails on ownership rather than on anything a
render could show.

`commerce.name` therefore keeps its job — it is the Service name, and so the
string §10.2's route file and §9.7's pricing hop dial — and this is what
Deployments and ConfigMaps are called. On the stable release the two are the
same string, which is why nothing before PR-25 needed the distinction.
*/}}
{{- define "commerce.instanceName" -}}
{{- if .Values.canary.enabled -}}
{{ include "commerce.name" . }}-canary
{{- else -}}
{{ include "commerce.name" . }}
{{- end -}}
{{- end -}}

{{- /*
A Deployment's selector, which is the Service's PLUS the track.

**The two Deployments must not select each other's pods.** With identical
selectors each would count the other's pods as its own and scale them away, so
the track has to be in here — and it must NOT be in `commerce.selectorLabels`,
because that one is the Service's and a Service that selected only `stable`
would send the canary no traffic at all. One label, in exactly one of the two
places, is the whole mechanism.

**This field is immutable, so adding it is a breaking change to an installed
release** — the API server refuses the update and the Deployment has to be
deleted and recreated. It costs nothing today because nothing anywhere has
installed these charts, and it would cost a downtime window later. That is the
argument for taking it now rather than when a canary is first wanted.
*/}}
{{- define "commerce.deploymentSelectorLabels" -}}
{{ include "commerce.selectorLabels" . }}
app.kubernetes.io/track: {{ include "commerce.track" . }}
{{- end -}}

{{/*
The migration Job's POD labels, which must NOT match the Service selector.

A Service selects pods, and the migration Job's pod template carried
`commerce.labels` — which contains `commerce.selectorLabels` verbatim. So for
the length of every `pre-upgrade` hook, the migrator became an endpoint of the
service it was migrating: a pod with a database connection, no HTTP listener,
and a share of live traffic being routed to it. Measured in the render, not
inferred — `catalog-api`'s Service selector and the Job's pod labels were the
same two lines.

The same match put it inside the PodDisruptionBudget, so a one-shot pod counted
toward the availability of a service it does not serve.

`-migrate` on the name is what breaks both, and `component` says what the pod
is for anyone reading `kubectl get pods -L`. The Job OBJECT keeps the ordinary
labels: object labels are not what endpoints are computed from, and losing the
identity there would cost the one thing these labels are for.
*/}}
{{- define "commerce.migrationPodLabels" -}}
app.kubernetes.io/name: {{ include "commerce.name" . }}-migrate
app.kubernetes.io/part-of: commerce
app.kubernetes.io/component: migrator
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ include "commerce.tag" . | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | quote }}
{{- end -}}

{{- /*
The full label set for object metadata. Free to carry release-scoped and
version-scoped labels, because unlike the selector above nothing here is
immutable.
*/}}
{{- define "commerce.labels" -}}
{{ include "commerce.selectorLabels" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- /*
Not truncated: `commerce.tag` refuses a tag longer than 63 outright, for the
reason _migration-job.tpl gives about its own name. Truncating produced a
version label naming a tag no registry has, and could not be made safe — a cut
can land on a dot, which `trimSuffix "-"` never touched.
*/}}
app.kubernetes.io/version: {{ include "commerce.tag" . | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | quote }}
{{- end -}}

{{- /*
The non-secret half of §15.4's inventory: everything whose Kind column reads
Config, rendered into the ConfigMap the Deployment mounts with `envFrom`.

The split is §15.4's own and it is mechanical — **if the value contains a
credential, it is a Secret** — so the two halves of this file are that table
read down its Kind column, and a key in the wrong one is a password in a
ConfigMap or a plain string in a Secret.
*/}}
{{- define "commerce.config" -}}
{{- /*
§15.5's canary needs the two tracks to be distinguishable in the telemetry, and
this is the line that makes them so.

OTEL_RESOURCE_ATTRIBUTES is the OpenTelemetry SDK's OWN mechanism: the resource
builder AddObservability configures already honours it, so this adds an
attribute without Common.Web knowing the word "canary". Asserted end to end in
`ObservabilityTests.The_resource_carries_the_deployment_track_the_environment_supplies`,
against the resource a real host exports rather than against the variable.

`service.version` was the obvious discriminator and is not one. BuildInfo
strips the source-revision suffix deliberately — "a value that changes every
commit turns one series into thousands" — and nothing in this solution sets an
assembly version, so every build in the platform reports 1.0.0. A registered
name is not a live signal.

Two values only, so the cardinality cost is one extra series per track.
*/}}
OTEL_RESOURCE_ATTRIBUTES: {{ printf "deployment.track=%s" (include "commerce.track" .) | quote }}
Identity__Authority: {{ include "commerce.require" (list .Values.identity.authority "identity.authority is required for every host, the gateway included (§15.4) — AddJwtAuthentication reads it eagerly and throws naming the key, so an unset value is a pod that never starts.") | quote }}
OTEL_EXPORTER_OTLP_ENDPOINT: {{ include "commerce.require" (list .Values.observability.otlpEndpoint "observability.otlpEndpoint is required: UseOtlpExporter reads the OpenTelemetry standard variable, and left unset it exports to localhost:4317, where nothing listens in a pod (§15.4).") | quote }}
{{- if .Values.identity.clientCredentials }}
{{- /*
Two of the three client-credential keys are Config and only the secret is a
Secret (§15.4). They belong to the one host that calls a peer synchronously
(§9.7, ADR-017); all three are [Required] on ServiceIdentityOptions and gated
by ValidateOnStart, so a missing one is a refusal to boot rather than a 401
somebody reads as the callee's fault.

A second chart growing these is a design change, not a configuration change.

**The switch is its own key, and `clientId` used to be it.** That made the
opt-out invalid rather than merely odd: clearing `identity.clientId` on the BFF
dropped all three keys, and `Web.Bff` binds `ServiceIdentityOptions`
unconditionally — so the release rendered, rolled, and the pod refused to
start. A whitespace-only value did the same while slipping past
`commerce.require`, because a truthiness test is not a requirement. §15.4 calls
this the *required-for-some-hosts* category; an explicit boolean is what makes
a host say which it is, and every value below is then required rather than
implied.
*/}}
Identity__Client__ClientId: {{ include "commerce.require" (list .Values.identity.clientId "identity.clientId is required when identity.clientCredentials: Web.Bff binds ServiceIdentityOptions unconditionally and ValidateOnStart refuses to boot without it (§15.4).") | quote }}
Identity__Client__Scope: {{ include "commerce.require" (list .Values.identity.scope "identity.scope is required when identity.clientCredentials: it becomes the audience every service validates (§11.5), and ServiceIdentityOptions marks it [Required].") | quote }}
{{- end }}
{{- end -}}

{{- /*
The secret half of the same table, and the rule that decides what is in either:
a variable joins when a host's code READS it, and not before.

That is the rule §14.1's Compose blocks already state — "an env var nothing
reads is the container form of an unused registration". §15.4's inventory marks
`ConnectionStrings__RedisCache` and `ConnectionStrings__RedisCoordination`
required *once the host calls `AddRedisConnections`* — **both or neither** —
and **that condition is now met**: §8.5's PR gave that helper its first
callers, in Catalog and Ordering. So the two keys are rendered below, under
`redis.enabled`, exactly as `Identity__Authority` joined with PR-16.

**The condition is the CALL, not a cache read, and this paragraph said "once a
host reads a cache" until a review caught it.** The helper reads both
connection strings eagerly, so a host that caches nothing still fails at
startup without them — and §8.5's idempotency store reads the *coordination*
instance, which is not a cache at all and is the `noeviction` half of §8.1's
split. Keying the requirement on caching would leave the one key this PR
actually made load-bearing looking optional.

**This paragraph said the opposite in the branch that added them**, which is
the drift the rule above exists to prevent arriving inside the file it governs:
the keys went in eighty lines down and the comment explaining their absence
stayed where it was.

Secrets are REFERENCED, never rendered. External Secrets Operator owns the
Secret objects (§15.4); a chart that templated a connection string would put a
password into `helm get values` and into every diff of this repository.
*/}}
{{- define "commerce.env" -}}
{{- /*
A CAPABILITY IS A FACT ABOUT THE CODE, NOT AN ENVIRONMENT SETTING, and these
flags were free values until Copilot pointed at six of them at once.

`Catalog.Infrastructure` always calls `GetConnectionString("Catalog")` and
always registers MassTransit; `Web.Bff` always binds `ServiceIdentityOptions`
with `ValidateOnStart`. So `database.enabled: false` on Catalog, or
`clientCredentials: false` on the BFF, is not a smaller deployment — it is a
clean render followed by a pod that will not start, which is the exact shape
every guard in this file exists to refuse.

Helm has no immutable value, so the guard is coherence instead: a chart that
carries the SETTINGS for a capability may not disable it. Only a chart that
never had them can be off, which is what makes the gateway's `enabled: false`
lines honest and an overlay's a refusal.

The schema was the other candidate — a `chart:` block naming each capability,
replacing these flags. It is the better shape and it is not this PR's: it
renames keys §15.3 prints, at round six of a review, and the coherence check
closes the same six holes without moving anything a reader has been told to
look for.
*/}}
{{- /*
On Catalog and Ordering the migration Job's own coherence check reaches this
first and reports a sharper message, so what surfaces there is "a migrator with
no database is incoherent". This is the general case behind it: a chart with a
connection name and no migrator — which nothing in the platform is yet, and
which Shipping and Notifications will not be either — still may not disable the
database its host unconditionally resolves.
*/}}
{{- if and .Values.database.connectionName (not .Values.database.enabled) }}
{{- fail "database.enabled is false but database.connectionName is set. A service that carries a connection name reads one at startup (§7.1) — disabling it renders cleanly and produces a pod that cannot resolve its own database. A capability is a fact about the code, not an environment setting." }}
{{- end }}
{{- if and .Values.broker.secretRef (not .Values.broker.enabled) }}
{{- fail "broker.enabled is false but broker.secretRef is set. AddMassTransitMessaging throws without ConnectionStrings:RabbitMq (§9.3), so this renders cleanly and the host does not start." }}
{{- end }}
{{- if and .Values.redis.secretRef (not .Values.redis.enabled) }}
{{- fail "redis.enabled is false but redis.secretRef is set. AddRedisConnections reads BOTH connection strings eagerly and throws naming the missing one (§8.1), so this renders cleanly and the host does not start. A capability is a fact about the code, not an environment setting." }}
{{- end }}
{{- if and .Values.identity.clientId (not .Values.identity.clientCredentials) }}
{{- fail "identity.clientCredentials is false but identity.clientId is set. Web.Bff binds ServiceIdentityOptions unconditionally and ValidateOnStart refuses to boot without all three values (§15.4) — so this is a render that succeeds and a pod that never starts." }}
{{- end }}
{{- if .Values.database.enabled }}
{{- /*
The RUNTIME connection string (DML only) — §7.1's split identity. The migrator
key is the other half, mounted into the migration Job and nowhere else.
*/}}
- name: ConnectionStrings__{{ include "commerce.require" (list .Values.database.connectionName "database.connectionName is required when database.enabled: it is the .NET configuration key this service's Infrastructure passes to GetConnectionString (§7.1), and it differs per service.") }}
  valueFrom:
    secretKeyRef:
      name: {{ include "commerce.require" (list .Values.database.runtimeSecretRef.name "database.runtimeSecretRef.name is required when database.enabled.") | quote }}
      key: {{ include "commerce.require" (list .Values.database.runtimeSecretRef.key "database.runtimeSecretRef.key is required when database.enabled.") | quote }}
{{- end }}
{{- if .Values.broker.enabled }}
- name: ConnectionStrings__RabbitMq
  valueFrom:
    secretKeyRef:
      name: {{ include "commerce.require" (list .Values.broker.secretRef.name "broker.secretRef.name is required when broker.enabled: AddMassTransitMessaging throws without the connection string, so the host does not start (§9.3).") | quote }}
      key: {{ include "commerce.require" (list .Values.broker.secretRef.key "broker.secretRef.key is required when broker.enabled.") | quote }}
{{- end }}
{{- if .Values.redis.enabled }}
{{- if eq (.Values.redis.secretRef.cacheKey | toString) (.Values.redis.secretRef.coordinationKey | toString) }}
{{- fail "redis.secretRef.cacheKey and redis.secretRef.coordinationKey are the same key. The two instances have different eviction policies (§8.1) — one key points both connections at the same server, and if that is the allkeys-lru instance then §8.5's idempotency claims are evicted under exactly the memory pressure that makes a duplicate write hardest to reproduce. A capability is a fact about the code, not an environment setting." }}
{{- end }}
{{- /*
The guard above was argued in the comment below and not written, until a review
asked what enforced it. Nothing did: the smoke test renders this repository's
own values, which differ, so every render was green and a production overlay
setting both to the cache key would have been too. **An argument in a comment
is not a control**, and the failure it describes is the one that cannot be
reproduced afterwards — an evicted claim leaves no trace of having existed.
*/}}
{{- /*
§8.1's two connections, and BOTH are required even where only one is read.
AddRedisConnections is one call by design (§8.2) and reads both eagerly, so a
service either has Redis or does not — half-having it is a pod that will not
start, which is the same shape as every other guard in this file.

Secrets rather than Config, unlike the authority and the OTLP endpoint above,
and §8.1 is why: each service connects as its OWN ACL user, so the string
carries a credential. Two references rather than one, because the two instances
have different eviction policies and therefore different servers (§8.1) — a
single value would let a chart point idempotency keys at the allkeys-lru
instance, where they are evicted under exactly the memory pressure that makes
the duplicate write hardest to reproduce.
*/}}
- name: ConnectionStrings__RedisCache
  valueFrom:
    secretKeyRef:
      name: {{ include "commerce.require" (list .Values.redis.secretRef.name "redis.secretRef.name is required when redis.enabled: AddRedisConnections throws without both connection strings, so the host does not start (§8.1).") | quote }}
      key: {{ include "commerce.require" (list .Values.redis.secretRef.cacheKey "redis.secretRef.cacheKey is required when redis.enabled.") | quote }}
- name: ConnectionStrings__RedisCoordination
  valueFrom:
    secretKeyRef:
      name: {{ include "commerce.require" (list .Values.redis.secretRef.name "redis.secretRef.name is required when redis.enabled.") | quote }}
      key: {{ include "commerce.require" (list .Values.redis.secretRef.coordinationKey "redis.secretRef.coordinationKey is required when redis.enabled: it is the noeviction instance §8.5's idempotency claims are written to, and pointing it at the cache one is a duplicate write nobody can reproduce (§8.1).") | quote }}
{{- end }}
{{- if .Values.identity.clientCredentials }}
- name: Identity__Client__ClientSecret
  valueFrom:
    secretKeyRef:
      name: {{ include "commerce.require" (list .Values.identity.clientSecretRef.name "identity.clientSecretRef.name is required when identity.clientCredentials. The secret is a reference, never a value (§15.3).") | quote }}
      key: {{ include "commerce.require" (list .Values.identity.clientSecretRef.key "identity.clientSecretRef.key is required when identity.clientCredentials.") | quote }}
{{- end }}
{{- end -}}
