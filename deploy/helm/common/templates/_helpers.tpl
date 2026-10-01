{{/*
`required` fails on nil and the empty string but passes `" "`, which
`AddJwtAuthentication` treats as missing, so this trims first and a blank
value counts as missing. `toString` comes before `trim` because `trim`
errors on a non-string, and `tag: 1.2` is a YAML float.
*/}}
{{- define "commerce.require" -}}
{{- $value := index . 0 -}}
{{- $message := index . 1 -}}
{{- required $message ($value | default "" | toString | trim) -}}
{{- end -}}

{{/*
Non-blank is not an address either: each caller is a base address its host
parses before it will start, refusing far more than the empty string, so
this checks the shape. HTTPS unconditionally, where the hosts require it
only outside Development: a chart sets no environment, so Production runs.
*/}}
{{- define "commerce.requireUrl" -}}
{{- $value := index . 0 -}}
{{- $message := index . 1 -}}
{{- $url := include "commerce.require" (list $value $message) -}}
{{- /*
One regex rather than a parse, because Helm has no URL type: HTTPS, a host of
letters, digits, dots, hyphens and underscores, an optional numeric port and
an optional path, which leaves out user information, a query, a fragment, a
wildcard and an IPv6 literal. Not a copy of the hosts' rule, which is built on
`Uri.TryCreate`: an empty label, for one, renders and is refused at startup.
*/}}
{{- if not (regexMatch "^https://[\\p{L}\\p{N}\\p{M}._-]+(:[0-9]+)?(/[^?#]*)?$" $url) }}
{{- fail (printf "%s The value is not an HTTPS address this chart will accept: a host of letters, digits, dots, hyphens and underscores, optionally a numeric port, and optionally a path. A query, a fragment, a wildcard and a non-numeric port are refused here rather than at startup, and user information and an IPv6 literal are refused outright (§15.4)." $message) }}
{{- end }}
{{- /*
The port's range, which the digits above do not bound: `Uri.TryCreate`
refuses `:65536` at startup, and nothing listens on `:0`. Unlike
`edge-config.yaml` there is no canonical-spelling check: a base address is
parsed rather than compared as text, so the host accepts `:08443`.
*/}}
{{- $port := regexFind ":[0-9]+$" (regexFind "^https://[^/]+" $url) }}
{{- if $port }}
{{- $n := atoi (trimPrefix ":" $port) }}
{{- if or (lt $n 1) (gt $n 65535) }}
{{- fail (printf "%s Its port is outside 1-65535 (§15.4)." $message) }}
{{- end }}
{{- end }}
{{- $url -}}
{{- end -}}

{{- /*
The Service name, which every object's name is built from and which is not
derived from the release: the gateway's route file (§10.2) and the BFF's
pricing hop dial it as a literal, so a release-derived one would make an
umbrella install a 502 rather than a template error.
*/}}
{{- define "commerce.name" -}}
{{- include "commerce.require" (list .Values.workload.name "workload.name is required: it is this deployable's Service name, and therefore the string the gateway's route file and the BFF's pricing hop dial (§10.2, §9.7).") -}}
{{- end -}}

{{- /*
The image tag, required rather than defaulted: values.yaml leaves it empty
on purpose, so a deploy that cannot name its image fails rather than rolls
one nobody chose (§15.3).
*/}}
{{- define "commerce.tag" -}}
{{- $tag := include "commerce.require" (list .Values.image.tag "image.tag is required and values.yaml leaves it empty on purpose: a deploy that cannot name its image must fail rather than roll something nobody chose (§15.3). CI supplies it; a config-only deploy resolves the running tag first (§15.1).") -}}
{{- /*
The tag also names the migration Job and `app.kubernetes.io/version`, so it
must be valid as both: each dot-separated segment a DNS-1123 label. It is
validated rather than sanitised, so the label names the image that runs.
*/}}
{{- range $segment := splitList "." $tag }}
{{- if not (regexMatch "^[a-z0-9]([a-z0-9-]*[a-z0-9])?$" $segment) }}
{{- fail (printf "image.tag %q is not usable as Kubernetes metadata: the segment %q is not a DNS-1123 label. The tag becomes the migration Job's name and app.kubernetes.io/version, so each dot-separated segment must be lowercase alphanumerics and dashes, starting and ending alphanumeric — which every commit SHA and ordinary semver already is (§15.3)." $tag $segment) }}
{{- end }}
{{- end }}
{{- /*
And the length: `app.kubernetes.io/version` carries the tag on every chart,
and a label value may not exceed 63 characters.
*/}}
{{- if gt (len $tag) 63 }}
{{- fail (printf "image.tag is %d characters. It becomes app.kubernetes.io/version, and a label value may not exceed 63 (§15.3)." (len $tag)) }}
{{- end }}
{{- $tag -}}
{{- end -}}

{{- /*
The selector carries the workload name and nothing release-derived: these
pods are found by the Service name §10.2's route file and §9.7's pricing
hop dial, and a Deployment's selector cannot change once it exists.
*/}}
{{- define "commerce.selectorLabels" -}}
app.kubernetes.io/name: {{ include "commerce.name" . }}
app.kubernetes.io/part-of: commerce
{{- end -}}

{{- /*
§15.5's canary is a second release of this chart, with `canary.enabled`, its
own replicas and its own tag. Both tracks answer to one Service, so traffic
splits by pod count; `deploy/canary/canary.py` turns a weight into pods. A
rollback removes the canary's pods and leaves the schema its migration hook
applied, which §15.5's backward-compatibility rule covers (ADR-022).
*/}}
{{- define "commerce.track" -}}
{{- if .Values.canary.enabled }}canary{{ else }}stable{{ end -}}
{{- end -}}

{{- /*
The name of what this release owns. Helm refuses to touch another release's
objects, so the canary cannot render the stable release's Deployment;
`commerce.name` stays the Service name, and the two differ only on a canary.
*/}}
{{- define "commerce.instanceName" -}}
{{- if .Values.canary.enabled -}}
{{ include "commerce.name" . }}-canary
{{- else -}}
{{ include "commerce.name" . }}
{{- end -}}
{{- end -}}

{{- /*
A Deployment's selector: the Service's plus the track, so neither track's
Deployment adopts the other's pods, while the Service, which selects without
it, sends traffic to both. It is immutable, so changing it means recreating
the Deployment.
*/}}
{{- define "commerce.deploymentSelectorLabels" -}}
{{ include "commerce.selectorLabels" . }}
app.kubernetes.io/track: {{ include "commerce.track" . }}
{{- end -}}

{{/*
The migration Job's pod labels, which must not match the Service selector:
`commerce.labels` would make the migrator an endpoint of the service it
migrates and count it in the PodDisruptionBudget. The Job object keeps the
ordinary labels, since endpoints are computed from pods.
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
Not truncated: `commerce.tag` refuses a tag over 63 characters, because a
cut would name a tag no registry has.
*/}}
app.kubernetes.io/version: {{ include "commerce.tag" . | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | quote }}
{{- end -}}

{{- /*
The non-secret half of §15.4's inventory, mounted with `envFrom`: a value
that contains a credential is a Secret, so this half is the Config rows.
*/}}
{{- define "commerce.config" -}}
{{- /*
§15.5's canary needs the tracks distinguishable in telemetry. The SDK's own
OTEL_RESOURCE_ATTRIBUTES adds the attribute without Common.Web knowing the
word; `service.version` cannot, since every build reports 1.0.0. Two values,
so one extra series per track.
*/}}
OTEL_RESOURCE_ATTRIBUTES: {{ printf "deployment.track=%s" (include "commerce.track" .) | quote }}
Identity__Authority: {{ include "commerce.requireUrl" (list .Values.identity.authority "identity.authority is required for every host, the gateway included (§15.4) — AddJwtAuthentication reads it eagerly and throws naming the key, so an unset value is a pod that never starts.") | quote }}
OTEL_EXPORTER_OTLP_ENDPOINT: {{ include "commerce.require" (list .Values.observability.otlpEndpoint "observability.otlpEndpoint is required: UseOtlpExporter reads the OpenTelemetry standard variable, and left unset it exports to localhost:4317, where nothing listens in a pod (§15.4).") | quote }}
{{- if .Values.identity.clientCredentials }}
{{- /*
Two of the three client-credential keys are Config; the secret is a Secret
(§15.4). The BFF and Shipping's worker, the hosts that call a peer, bind
ServiceIdentityOptions with ValidateOnStart, so a missing key refuses to
boot. The switch is an explicit boolean, so each value under it is required.
*/}}
Identity__Client__ClientId: {{ include "commerce.require" (list .Values.identity.clientId "identity.clientId is required when identity.clientCredentials: the hosts that declare it bind ServiceIdentityOptions unconditionally and ValidateOnStart refuses to boot without it (§15.4).") | quote }}
Identity__Client__Scope: {{ include "commerce.require" (list .Values.identity.scope "identity.scope is required when identity.clientCredentials: it becomes the audience every service validates (§11.5), and ServiceIdentityOptions marks it [Required].") | quote }}
{{- end }}
{{- if (.Values.paymentProvider).enabled }}
{{- /*
§3.2's payment provider's address: Config, since an address is not a
credential, and refused at render because the host refuses it at start.
`(.Values.paymentProvider)` reads a missing map as empty on the charts with
no such block, where the dotted form fails the render.
*/}}
PaymentProvider__BaseUrl: {{ include "commerce.requireUrl" (list .Values.paymentProvider.baseUrl "paymentProvider.baseUrl is required when paymentProvider.enabled: AddPaymentProvider reads it eagerly and throws naming the key, so the host does not start (§15.4).") | quote }}
{{- end }}
{{- if (.Values.carrier).enabled }}
{{- /*
§3.2's carrier, on the provider's pattern one service over and for the same
reason: an address is not a credential, so it is Config, and it is required
because the host parses it before it will start (§15.4). HTTPS unconditionally,
which `commerce.requireUrl` argues — a chart is how a cluster is deployed and
sets no environment, so Production is what runs.
*/}}
Carrier__BaseUrl: {{ include "commerce.requireUrl" (list .Values.carrier.baseUrl "carrier.baseUrl is required when carrier.enabled: Shipping's carrier registration reads it eagerly and throws naming the key, so the host does not start (§15.4).") | quote }}
{{- end }}
{{- if (.Values.addressSource).enabled }}
{{- /*
ADR-052's address read, which `requireUrl` must not see: TLS ends at the
Ingress (§10.1), so Ordering's HTTP/2-only endpoint is dialled over cleartext,
as `PricingHop.cs` dials Catalog's, and the host's guard accepts either scheme.
The shape and the port range are `requireUrl`'s, with the scheme widened.
*/}}
{{- $addressSource := include "commerce.require" (list .Values.addressSource.baseUrl "addressSource.baseUrl is required when addressSource.enabled: the worker resolves the address owner eagerly (ADR-052) and does not start without it (§15.4).") }}
{{- if not (regexMatch "^https?://[\\p{L}\\p{N}\\p{M}._-]+(:[0-9]+)?(/[^?#]*)?$" $addressSource) }}
{{- fail "addressSource.baseUrl is not an address this chart will accept: http or https, a host of letters, digits, dots, hyphens and underscores, optionally a numeric port, and optionally a path. User information, a query, a fragment and a wildcard are refused here rather than at startup (§15.4)." }}
{{- end }}
{{- $addressPort := regexFind ":[0-9]+$" (regexFind "^https?://[^/]+" $addressSource) }}
{{- if $addressPort }}
{{- $n := atoi (trimPrefix ":" $addressPort) }}
{{- if or (lt $n 1) (gt $n 65535) }}
{{- fail "addressSource.baseUrl's port is outside 1-65535 (§15.4)." }}
{{- end }}
{{- end }}
AddressSource__BaseUrl: {{ $addressSource | quote }}
{{- end }}
{{- if (.Values.jurisdiction).enabled }}
{{- /*
ADR-053's two statutory windows, required and never defaulted: a window is a
fact about where a deployment runs. Each must read as a TimeSpan, because
`30 days` renders and fails binding in the new pod; the range is the host's
to refuse, since ShippingJurisdictionOptions owns its bounds.
*/}}
{{- $windows := dict
    "addressRetention" (include "commerce.require" (list .Values.jurisdiction.addressRetention "jurisdiction.addressRetention is required when jurisdiction.enabled: ADR-053 makes the window a value the deployment is given, and ShippingJurisdictionOptions refuses to boot without it."))
    "trackingRetention" (include "commerce.require" (list .Values.jurisdiction.trackingRetention "jurisdiction.trackingRetention is required when jurisdiction.enabled: ADR-053's second window, on the same terms.")) }}
{{- range $key, $window := $windows }}
{{- if not (regexMatch (include "commerce.timeSpanPattern" $) $window) }}
{{- fail (printf "jurisdiction.%s is %q, which is not a TimeSpan this chart will accept: [d.]hh:mm[:ss], as in 30.00:00:00 for thirty days. ShippingJurisdictionOptions binds it at start (ADR-053)." $key $window) }}
{{- end }}
{{- end }}
Jurisdiction__AddressRetention: {{ $windows.addressRetention | quote }}
Jurisdiction__TrackingRetention: {{ $windows.trackingRetention | quote }}
{{- end }}
{{- if (.Values.fulfilment).enabled }}
{{- /*
ADR-052's give-up age, on the jurisdiction windows' TimeSpan terms: present is
not enough, because `3 days` renders and fails binding in the new pod. The
range is the host's to refuse: FulfilmentOptions owns its bounds.
*/}}
{{- $giveUpAge := include "commerce.require" (list .Values.fulfilment.giveUpAge "fulfilment.giveUpAge is required when fulfilment.enabled: ADR-052 makes the give-up age a value the deployment is given, and FulfilmentOptions refuses to boot without it.") }}
{{- if not (regexMatch (include "commerce.timeSpanPattern" .) $giveUpAge) }}
{{- fail (printf "fulfilment.giveUpAge is %q, which is not a TimeSpan this chart will accept: [d.]hh:mm[:ss], as in 3.00:00:00 for three days. FulfilmentOptions binds it at start (ADR-052)." $giveUpAge) }}
{{- end }}
Fulfilment__GiveUpAge: {{ $giveUpAge | quote }}
{{- end }}
{{- end -}}

{{- /*
A .NET TimeSpan as this chart accepts one, `[d.]hh:mm[:ss[.fffffff]]` with hours
under 24 and minutes and seconds under 60, held once for every setting that
binds one. Hours under 24 because the binder reads `72:00:00` as seventy-two
days, silently.
*/}}
{{- define "commerce.timeSpanPattern" -}}
^([0-9]+\.)?([01]?[0-9]|2[0-3]):[0-5][0-9](:[0-5][0-9](\.[0-9]{1,7})?)?$
{{- end -}}

{{- /*
The secret half of the same table. A variable joins when a host's code reads
it, and a secret is referenced, never rendered: External Secrets Operator
owns the Secret objects (§15.4), and a rendered one would put a password
into `helm get values`.
*/}}
{{- define "commerce.env" -}}
{{- /*
A capability is a fact about the code, not an environment setting: Catalog
always resolves its database and the BFF always binds ServiceIdentityOptions,
so disabling either renders and the pod does not start. Helm has no
immutable value, so a chart carrying a capability's settings may not disable
it.
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
{{- fail "identity.clientCredentials is false but identity.clientId is set. The hosts that declare it bind ServiceIdentityOptions unconditionally and ValidateOnStart refuses to boot without all three values (§15.4) — so this is a render that succeeds and a pod that never starts." }}
{{- end }}
{{- if and (or (.Values.paymentProvider).apiKeySecretRef (.Values.paymentProvider).baseUrl) (not (.Values.paymentProvider).enabled) }}
{{- fail "paymentProvider.enabled is false but a paymentProvider setting is set. AddPaymentProvider reads both provider keys eagerly (§15.4), so this renders cleanly and the host does not start. A capability is a fact about the code, not an environment setting." }}
{{- end }}
{{- if and (or (.Values.carrier).apiKeySecretRef (.Values.carrier).baseUrl) (not (.Values.carrier).enabled) }}
{{- fail "carrier.enabled is false but a carrier setting is set. Shipping reads both carrier keys eagerly (§15.4), so this renders cleanly and the host does not start. A capability is a fact about the code, not an environment setting." }}
{{- end }}
{{- if and (.Values.addressSource).baseUrl (not (.Values.addressSource).enabled) }}
{{- fail "addressSource.enabled is false but addressSource.baseUrl is set. The worker resolves ADR-052's address owner at startup, so this renders cleanly and the host does not start." }}
{{- end }}
{{- if and (or (.Values.jurisdiction).addressRetention (.Values.jurisdiction).trackingRetention) (not (.Values.jurisdiction).enabled) }}
{{- fail "jurisdiction.enabled is false but a jurisdiction window is set. ShippingJurisdictionOptions is validated at start (ADR-053), so this renders cleanly and the host does not start." }}
{{- end }}
{{- if and (.Values.fulfilment).giveUpAge (not (.Values.fulfilment).enabled) }}
{{- fail "fulfilment.enabled is false but fulfilment.giveUpAge is set. FulfilmentOptions is validated at start (ADR-052), so this renders cleanly and the host does not start." }}
{{- end }}
{{- /*
The other direction moves a credential: Helm accepts values a chart never
declares, so `paymentProvider.enabled` on another chart would mount Payments'
provider Secret into a pod that never reads it. These name the owning
charts, so a further chart growing one is a design change made here.
*/}}
{{- if and (.Values.paymentProvider).enabled (ne .Chart.Name "payments") }}
{{- fail (printf "paymentProvider.enabled is true on the %s chart, and only payments registers a provider (§3.2). This would mount the provider's Secret into a pod that never reads it — a credential crossing a service boundary, which no value in an environment file may do." .Chart.Name) }}
{{- end }}
{{- if and (.Values.carrier).enabled (ne .Chart.Name "shipping") }}
{{- fail (printf "carrier.enabled is true on the %s chart, and only shipping books with a carrier (§3.2). This would mount the carrier's Secret into a pod that never reads it — a credential crossing a service boundary, which no value in an environment file may do." .Chart.Name) }}
{{- end }}
{{- if and .Values.identity.clientCredentials (not (has .Chart.Name (list "web-bff" "shipping"))) }}
{{- fail (printf "identity.clientCredentials is true on the %s chart, and the two hosts that call a peer synchronously are the BFF (§9.7, ADR-017) and Shipping's worker (ADR-052). This would mount one of their client secrets into a pod that never presents it — a credential crossing a service boundary, which no value in an environment file may do." .Chart.Name) }}
{{- end }}
{{- if .Values.database.enabled }}
{{- /*
The runtime connection string (DML only) — §7.1's split identity. The migrator
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
§8.1's two connections, both required because AddRedisConnections reads both
eagerly. Secrets, since each service connects as its own ACL user, and two
references because the instances differ in eviction policy, which is why
the guard above refuses one key for both.
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
{{- if (.Values.paymentProvider).enabled }}
- name: PaymentProvider__ApiKey
  valueFrom:
    secretKeyRef:
      name: {{ include "commerce.require" (list .Values.paymentProvider.apiKeySecretRef.name "paymentProvider.apiKeySecretRef.name is required when paymentProvider.enabled. The key is a reference, never a value (§15.3).") | quote }}
      key: {{ include "commerce.require" (list .Values.paymentProvider.apiKeySecretRef.key "paymentProvider.apiKeySecretRef.key is required when paymentProvider.enabled.") | quote }}
{{- end }}
{{- if (.Values.carrier).enabled }}
- name: Carrier__ApiKey
  valueFrom:
    secretKeyRef:
      name: {{ include "commerce.require" (list .Values.carrier.apiKeySecretRef.name "carrier.apiKeySecretRef.name is required when carrier.enabled. The key is a reference, never a value (§15.3).") | quote }}
      key: {{ include "commerce.require" (list .Values.carrier.apiKeySecretRef.key "carrier.apiKeySecretRef.key is required when carrier.enabled.") | quote }}
{{- end }}
{{- end -}}
