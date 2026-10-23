{{/* `required` fails on nil and the empty string but passes `" "`, which
`AddJwtAuthentication` treats as missing, so this trims first and a blank
value counts as missing. `toString` comes before `trim` because `trim`
errors on a non-string, and `tag: 1.2` is a YAML float. */}}
{{- define "commerce.require" -}}
{{- $value := index . 0 -}}
{{- $message := index . 1 -}}
{{- required $message ($value | default "" | toString | trim) -}}
{{- end -}}

{{/* Non-blank is not an address either: each caller is a base address its host
parses before it will start, refusing far more than the empty string, so
this checks the shape. HTTPS unconditionally, where the hosts require it
only outside Development: a chart sets no environment, so Production runs. */}}
{{- define "commerce.requireUrl" -}}
{{- $value := index . 0 -}}
{{- $message := index . 1 -}}
{{- $url := include "commerce.require" (list $value $message) -}}
{{- /* One regex rather than a parse, because Helm has no URL type: HTTPS, a host of
letters, digits, dots, hyphens and underscores, an optional numeric port and
an optional path, which leaves out user information, a query, a fragment, a
wildcard and an IPv6 literal. Not a copy of the hosts' rule, which is built on
`Uri.TryCreate`: an empty label, for one, renders and is refused at startup. */}}
{{- if not (regexMatch "^https://[\\p{L}\\p{N}\\p{M}._-]+(:[0-9]+)?(/[^?#]*)?$" $url) }}
{{- fail (printf "%s The value is not an HTTPS address this chart will accept: a host of letters, digits, dots, hyphens and underscores, optionally a numeric port, and optionally a path. A query, a fragment, a wildcard and a non-numeric port are refused here rather than at startup, and user information and an IPv6 literal are refused outright (§15.4)." $message) }}
{{- end }}
{{- /* The port's range, which the digits above do not bound: `Uri.TryCreate`
refuses `:65536` at startup, and nothing listens on `:0`. Unlike
`edge-config.yaml` there is no canonical-spelling check: a base address is
parsed rather than compared as text, so the host accepts `:08443`. */}}
{{- $port := regexFind ":[0-9]+$" (regexFind "^https://[^/]+" $url) }}
{{- if $port }}
{{- $n := atoi (trimPrefix ":" $port) }}
{{- if or (lt $n 1) (gt $n 65535) }}
{{- fail (printf "%s Its port is outside 1-65535 (§15.4)." $message) }}
{{- end }}
{{- end }}
{{- $url -}}
{{- end -}}

{{- /* The workload name, which every object's name is built from and which is not
derived from the release: where a peer dials a workload, through the
gateway's route file (§10.2), the BFF's pricing hop or Shipping's address
read (ADR-052), it dials this name as a literal, so a release-derived one
would leave an umbrella install dialling a name nothing answers to. */}}
{{- define "commerce.name" -}}
{{- include "commerce.require" (list .Values.workload.name "workload.name is required: every object this chart renders is named from it, the Service included where there is one, and a peer that dials this workload dials it as a literal (§10.2, §9.7).") -}}
{{- end -}}

{{- /* The image tag, required rather than defaulted: values.yaml leaves it empty
on purpose, so a deploy that cannot name its image fails rather than rolls
one nobody chose (§15.3). */}}
{{- define "commerce.tag" -}}
{{- $tag := include "commerce.require" (list .Values.image.tag "image.tag is required and values.yaml leaves it empty on purpose: a deploy that cannot name its image must fail rather than roll something nobody chose (§15.3). CI supplies it; a config-only deploy resolves the running tag first (§15.1).") -}}
{{- /* Where there is a migrator the tag also names its Job, so each dot-separated
segment must be a DNS-1123 label, and every chart holds the same shape. It
is validated rather than sanitised, so the `app.kubernetes.io/version`
label names the image that runs. */}}
{{- range $segment := splitList "." $tag }}
{{- if not (regexMatch "^[a-z0-9]([a-z0-9-]*[a-z0-9])?$" $segment) }}
{{- fail (printf "image.tag %q has the segment %q, which is not a DNS-1123 label. Every chart holds the tag to the shape the migration Job's name needs where there is one (§7.4), so each dot-separated segment must be lowercase alphanumerics and dashes, starting and ending alphanumeric — which every commit SHA and ordinary semver already is." $tag $segment) }}
{{- end }}
{{- end }}
{{- /* And the length: `app.kubernetes.io/version` carries the tag on every chart,
and a label value may not exceed 63 characters. */}}
{{- if gt (len $tag) 63 }}
{{- fail (printf "image.tag is %d characters. It becomes app.kubernetes.io/version, and a label value may not exceed 63." (len $tag)) }}
{{- end }}
{{- $tag -}}
{{- end -}}

{{- /* `@<digest>` when one is given, so the node pulls those bytes whatever the tag
names later; nothing when not, until a rollout can resolve one (ADR-081). */}}
{{- define "commerce.digest" -}}
{{- $digest := index . 0 | default "" | toString -}}
{{- if $digest }}
{{- if not (regexMatch "^sha256:[a-f0-9]{64}$" $digest) }}
{{- fail (printf "%s is %q, which is not a sha256 digest: it is sha256: and 64 lowercase hex characters, as a registry reports it (ADR-081)." (index . 1) $digest) }}
{{- end }}
{{- printf "@%s" $digest -}}
{{- end }}
{{- end -}}

{{- /* The selector carries the workload name and nothing release-derived: the
PodDisruptionBudget the stable release owns, and its Service where there is
one, select with it, and the canary is a second release whose pods they must
match (ADR-022). */}}
{{- define "commerce.selectorLabels" -}}
app.kubernetes.io/name: {{ include "commerce.name" . }}
app.kubernetes.io/part-of: commerce
{{- end -}}

{{- /* §15.5's canary is a second release of this chart, with `canary.enabled`, its
own replicas and its own tag. Where there is a Service both tracks answer to
it, so connections split by pod count, which `deploy/canary/canary.py` plans.
A rollback removes the canary's pods and, where there is a migrator, leaves
the schema its hook applied, which §15.5's backward-compatibility rule covers. */}}
{{- define "commerce.track" -}}
{{- if .Values.canary.enabled }}canary{{ else }}stable{{ end -}}
{{- end -}}

{{- /* ADR-104's switch: a chart whose `suspendable` is true may render
`suspended: true`, which stops the workload and leaves the migration Job. Every
other chart refuses it, and so does one with no `suspendable` at all, so a chart
added later is not suspendable until its owner says it is. smoke.sh holds each
chart's value to the ADR's table. */}}
{{- define "commerce.suspended" -}}
{{- if .Values.suspended -}}
{{- if not .Values.suspendable }}
{{- fail (printf "suspended is true on the %s chart, which ADR-104 does not class as suspendable: stopping it cancels orders or ends checkout, and the ADR names what each service costs while off." .Chart.Name) }}
{{- end }}
{{- if .Values.canary.enabled }}
{{- fail "suspended is true on the canary track. A canary judges a release by the traffic it receives, and a suspended release has none (ADR-022); suspend the stable release." }}
{{- end }}
true
{{- end -}}
{{- end -}}

{{- /* The name of what this release owns. Helm refuses to touch another release's
objects; `commerce.name` stays the workload name, and the two differ only on a canary. */}}
{{- define "commerce.instanceName" -}}
{{- if .Values.canary.enabled -}}
{{ include "commerce.name" . }}-canary
{{- else -}}
{{ include "commerce.name" . }}
{{- end -}}
{{- end -}}

{{- /* A Deployment's selector: the shared one plus the track, so neither track's
Deployment adopts the other's pods, while a Service, where there is one,
selects without the track and sends traffic to both. It is immutable, so
changing it means recreating the Deployment. */}}
{{- define "commerce.deploymentSelectorLabels" -}}
{{ include "commerce.selectorLabels" . }}
app.kubernetes.io/track: {{ include "commerce.track" . }}
{{- end -}}

{{/* The migration Job's pod labels, which must not match the shared selector:
`commerce.labels` would count the migrator in the PodDisruptionBudget and,
where there is a Service, make it an endpoint of the service it migrates.
The Job object keeps the ordinary labels, since endpoints come from pods. */}}
{{- define "commerce.migrationPodLabels" -}}
app.kubernetes.io/name: {{ include "commerce.name" . }}-migrate
app.kubernetes.io/part-of: commerce
app.kubernetes.io/component: migrator
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ include "commerce.tag" . | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | quote }}
{{- end -}}

{{- /* The full label set for object metadata. Free to carry release-scoped and
version-scoped labels, because unlike the selector above nothing here is
immutable. */}}
{{- define "commerce.labels" -}}
{{ include "commerce.selectorLabels" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- /* Not truncated: `commerce.tag` refuses a tag over 63 characters, because a
cut would name a tag no registry has. */}}
app.kubernetes.io/version: {{ include "commerce.tag" . | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | quote }}
{{- end -}}

{{- /* The non-secret half of §15.4's inventory, mounted with `envFrom`: a value
that contains a credential is a Secret, so this half is the Config rows. */}}
{{- define "commerce.config" -}}
{{- /* §15.5's canary needs the tracks distinguishable in telemetry. The SDK's own
OTEL_RESOURCE_ATTRIBUTES adds the attribute without Common.Web knowing the
word; `service.version` cannot, since every build reports 1.0.0. Two values,
so one extra series per track. */}}
OTEL_RESOURCE_ATTRIBUTES: {{ printf "deployment.track=%s" (include "commerce.track" .) | quote }}
Identity__Authority: {{ include "commerce.requireUrl" (list .Values.identity.authority "identity.authority is required for every host, the gateway included (§15.4) — AddJwtAuthentication reads it eagerly and throws naming the key, so an unset value is a pod that never starts.") | quote }}
{{- /* Not requireUrl, which is HTTPS only: the collector is in-cluster and plain HTTP by default. The scheme is
required, because the SDK reads the value as a URI and a bare host:port has the host as its scheme, and
commerce.urlPort cannot find its port. */}}
{{- $otlp := include "commerce.require" (list .Values.observability.otlpEndpoint "observability.otlpEndpoint is required: UseOtlpExporter reads the OpenTelemetry standard variable, and left unset it exports to localhost:4317, where nothing listens in a pod (§15.4).") }}
{{- if not (regexMatch "^https?://[\\p{L}\\p{N}\\p{M}._-]+(:[0-9]+)?(/[^?#]*)?$" $otlp) }}
{{- fail (printf "observability.otlpEndpoint is %q, which is not an http or https address this chart will accept, as in http://otel-collector.observability:4317. Without its scheme the exporter reads the host as one and exports nothing, and the NetworkPolicy opens port 80 for it (§15.4)." $otlp) }}
{{- end }}
{{- /* The port's range, which the digits above do not bound: the policy would carry it and the cluster refuse
it on apply. */}}
{{- $otlpPort := regexFind ":[0-9]+$" (regexFind "^https?://[^/]+" $otlp) }}
{{- if and $otlpPort (or (lt (atoi (trimPrefix ":" $otlpPort)) 1) (gt (atoi (trimPrefix ":" $otlpPort)) 65535)) }}
{{- fail (printf "observability.otlpEndpoint is %q, whose port is outside 1-65535 (§15.4)." $otlp) }}
{{- end }}
OTEL_EXPORTER_OTLP_ENDPOINT: {{ $otlp | quote }}
{{- if .Values.identity.clientCredentials }}
{{- /* Two of the three client-credential keys are Config; the secret is a Secret
(§15.4). The hosts that call out under a grant of their own (ADR-052) bind
ServiceIdentityOptions with ValidateOnStart, so a missing key refuses to
boot. The switch is an explicit boolean, so each value under it is required. */}}
Identity__Client__ClientId: {{ include "commerce.require" (list .Values.identity.clientId "identity.clientId is required when identity.clientCredentials: the hosts that declare it bind ServiceIdentityOptions unconditionally and ValidateOnStart refuses to boot without it (§15.4).") | quote }}
Identity__Client__Scope: {{ include "commerce.require" (list .Values.identity.scope "identity.scope is required when identity.clientCredentials: it is the scope the host's token is requested under (§11.5, ADR-052), and ServiceIdentityOptions marks it [Required].") | quote }}
{{- end }}
{{- if (.Values.paymentProvider).enabled }}
{{- /* §3.2's payment provider's address: Config, since an address is not a
credential, and refused at render because the host refuses it at start.
`(.Values.paymentProvider)` reads a missing map as empty on the charts with
no such block, where the dotted form fails the render. */}}
PaymentProvider__BaseUrl: {{ include "commerce.requireUrl" (list .Values.paymentProvider.baseUrl "paymentProvider.baseUrl is required when paymentProvider.enabled: AddPaymentProvider reads it eagerly and throws naming the key, so the host does not start (§15.4).") | quote }}
{{- end }}
{{- if (.Values.carrier).enabled }}
{{- /* §3.2's carrier, on the provider's terms above; HTTPS unconditionally, which
`commerce.requireUrl` argues. */}}
Carrier__BaseUrl: {{ include "commerce.requireUrl" (list .Values.carrier.baseUrl "carrier.baseUrl is required when carrier.enabled: Shipping's carrier registration reads it eagerly and throws naming the key, so the host does not start (§15.4).") | quote }}
{{- end }}
{{- if (.Values.addressSource).enabled }}
{{- /* ADR-052's address read, which `requireUrl` must not see: TLS ends at the
Ingress (§10.1), so Ordering's HTTP/2-only endpoint is dialled over cleartext,
as `PricingHop.cs` dials Catalog's, and the host's guard accepts either scheme.
The shape and the port range are `requireUrl`'s, with the scheme widened. */}}
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
{{- /* ADR-053 rule 1's kinds, none defaulted: a language set, the zone dates are
rendered in, ADR-120's required pairs, and every other member a statutory
window. Which members a host binds is its chart's to say, in capabilities.yaml,
since a member removed outright renders nothing here. */}}
{{- $jurisdiction := .Values.jurisdiction }}
{{- if hasKey $jurisdiction "languages" }}
{{- $languages := $jurisdiction.languages | default list }}
{{- if not (kindIs "slice" $languages) }}
{{- fail "jurisdiction.languages is not a list. ADR-053's language set renders one indexed key per language, as §15.4 spells a list." }}
{{- end }}
{{- if not $languages }}
{{- fail "jurisdiction.languages must hold at least one language: ADR-053 makes the set a value the deployment is given, and the host refuses an empty one at start." }}
{{- end }}
{{- range $i, $language := $languages }}
{{- $tag := include "commerce.require" (list $language (printf "jurisdiction.languages[%d] is blank: each entry is a language the deployment sends in (ADR-053)." $i)) }}
{{- if not (regexMatch "^[a-z]{2,3}(-[A-Za-z0-9]{1,8})*$" $tag) }}
{{- fail (printf "jurisdiction.languages[%d] is %q, which is not a language tag this chart will accept, as in en or kk. Whether the deployment ships it is the host's to refuse at start (ADR-053)." $i $tag) }}
{{- end }}
Jurisdiction__Languages__{{ $i }}: {{ $tag | quote }}
{{- end }}
{{- end }}
{{- if hasKey $jurisdiction "timeZone" }}
{{- $zone := include "commerce.require" (list $jurisdiction.timeZone "jurisdiction.timeZone is required when jurisdiction.enabled: ADR-053 makes the zone dates are rendered in a value the deployment is given.") }}
{{- if not (regexMatch "^[A-Za-z][A-Za-z0-9_+-]*(/[A-Za-z0-9_+-]+)*$" $zone) }}
{{- fail (printf "jurisdiction.timeZone is %q, which is not an IANA zone id this chart will accept, as in Europe/London. Whether the image knows the zone is the host's to refuse at start (ADR-053)." $zone) }}
{{- end }}
Jurisdiction__TimeZone: {{ $zone | quote }}
{{- end }}
{{- if hasKey $jurisdiction "required" }}
{{- /* ADR-120's duties, which an empty list states as none: configuration binds no
key for an empty list, so null is what this chart refuses as unstated. */}}
{{- if kindIs "invalid" $jurisdiction.required }}
{{- fail "jurisdiction.required is unstated. ADR-120 makes the topic/channel pairs a jurisdiction obliges the service to send a value the deployment is given, as in [order-placed/email]; state [] for one that requires none." }}
{{- end }}
{{- if not (kindIs "slice" $jurisdiction.required) }}
{{- fail "jurisdiction.required is not a list. ADR-120's pairs render one indexed key each, as §15.4 spells a list." }}
{{- end }}
{{- range $i, $pair := $jurisdiction.required }}
{{- $named := include "commerce.require" (list $pair (printf "jurisdiction.required[%d] is blank: each entry is a topic/channel pair (ADR-120)." $i)) }}
{{- if not (regexMatch "^[a-z][a-z0-9-]*/[a-z]+$" $named) }}
{{- fail (printf "jurisdiction.required[%d] is %q, which is not a topic/channel pair this chart will accept, as in order-placed/email. Whether the service knows the pair is the host's to refuse at start (ADR-120)." $i $named) }}
{{- end }}
Jurisdiction__Required__{{ $i }}: {{ $named | quote }}
{{- end }}
{{- end }}
{{- range $key, $window := omit $jurisdiction "enabled" "languages" "timeZone" "required" }}
{{- $value := include "commerce.timeSpan" (list $window (printf "jurisdiction.%s" $key) (printf "jurisdiction.%s is required when jurisdiction.enabled: ADR-053 makes every window a value the deployment is given, and the host's jurisdiction options refuse to boot without it." $key) "The host's jurisdiction options bind it at start (ADR-053).") }}
Jurisdiction__{{ upper (substr 0 1 $key) }}{{ substr 1 (len $key) $key }}: {{ $value | quote }}
{{- end }}
{{- end }}
{{- if (.Values.fulfilment).enabled }}
{{- /* ADR-052's give-up age for a pending shipment; FulfilmentOptions owns its range. */}}
Fulfilment__GiveUpAge: {{ include "commerce.timeSpan" (list .Values.fulfilment.giveUpAge "fulfilment.giveUpAge" "fulfilment.giveUpAge is required when fulfilment.enabled: ADR-052 makes the give-up age a value the deployment is given, and FulfilmentOptions refuses to boot without it." "FulfilmentOptions binds it at start (ADR-052).") | quote }}
{{- end }}
{{- if (.Values.privacy).enabled }}
{{- /* ADR-092's holders, one indexed key each as §15.4 spells a list, and the service
level. Neither is defaulted: the set decides when a request closes. The name
rule is the host's (ErasureRequest.IsResponderName), checked here so a mistake
fails the render and not the start. */}}
{{- $responders := (.Values.privacy).responders | default list }}
{{- if not (kindIs "slice" $responders) }}
{{- fail "privacy.responders is not a list. ADR-092's holders render one indexed key per name, as §15.4 spells a list." }}
{{- end }}
{{- if not $responders }}
{{- fail "privacy.responders must name at least one holder: a request closes when every holder in the set has answered (ADR-092), and the host refuses an empty set at start." }}
{{- end }}
{{- $seen := dict }}
{{- range $i, $responder := $responders }}
{{- $name := include "commerce.require" (list $responder (printf "privacy.responders[%d] is blank: each entry is a holder whose answer a request waits for (ADR-092)." $i)) }}
{{- if not (regexMatch "^[a-z0-9][a-z0-9-]{0,31}$" $name) }}
{{- fail (printf "privacy.responders[%d] is %q, which is not a holder name the host will accept: lower-case letters, digits and hyphens, at most 32, not starting with a hyphen (ADR-092)." $i $name) }}
{{- end }}
{{- if hasKey $seen $name }}
{{- fail (printf "privacy.responders names %q twice (ADR-092)." $name) }}
{{- end }}
{{- $_ := set $seen $name true }}
Privacy__Responders__{{ $i }}: {{ $name | quote }}
{{- end }}
{{- if gt (len (join "," $responders)) 400 }}
{{- fail "privacy.responders together are over 400 characters, the width a request stores the set in (ADR-092)." }}
{{- end }}
Privacy__CompletionSlo: {{ include "commerce.timeSpan" (list .Values.privacy.completionSlo "privacy.completionSlo" "privacy.completionSlo is required when privacy.enabled: it is the adopter's reading of the law (ADR-053), and the host refuses to boot without it." "PrivacyOptions binds it at start (ADR-092).") | quote }}
{{- end }}
{{- if (.Values.mail).enabled }}
{{- /* The relay's five Config keys (§15.4); its password is commerce.env's. A chart
sets no environment, so Production is what runs, where the host refuses plain
or anonymous submission: StartTls and a user name are therefore required. */}}
{{- $mail := .Values.mail }}
{{- $host := include "commerce.require" (list $mail.host "mail.host is required when mail.enabled: the relay is a value the deployment is given (ADR-053), and MailOptions refuses to boot without it (§15.4).") }}
{{- if not (regexMatch "^[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?(\\.[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?)*$" $host) }}
{{- fail (printf "mail.host is %q, which is not a host name this chart will accept: letters, digits, hyphens and dots, with no scheme, port or path — the port is mail.port (§15.4)." $host) }}
{{- end }}
{{- $port := include "commerce.require" (list $mail.port "mail.port is required when mail.enabled: MailOptions marks it [Required] (§15.4).") }}
{{- if or (not (regexMatch "^[0-9]+$" $port)) (lt (atoi $port) 1) (gt (atoi $port) 65535) }}
{{- fail (printf "mail.port is %q, which is not a port: a whole number in 1-65535 (§15.4)." $port) }}
{{- end }}
{{- $from := include "commerce.require" (list $mail.from "mail.from is required when mail.enabled: it is the one sender every message carries and the domain each Message-ID is minted under (§15.4).") }}
{{- /* A quoted string may hold a comma, an @ or an escaped quote, so each is set aside first; a quote left over is unbalanced. */}}
{{- if not (regexMatch "^[^,;\\r\\n@\"]*@[^,;\\r\\n@\"]+$" (regexReplaceAll "\"(?:[^\"\\\\\\r\\n]|\\\\.)*\"" $from "")) }}
{{- fail (printf "mail.from is %q, which is not one mailbox this chart will accept: one address with one @, optionally behind a display name, and no second address. MailOptions parses it at start (§15.4)." $from) }}
{{- end }}
{{- $security := include "commerce.require" (list $mail.security "mail.security is required when mail.enabled: MailOptions marks it [Required] (§15.4).") }}
{{- if ne $security "StartTls" }}
{{- fail (printf "mail.security is %q, and this chart accepts StartTls alone: the host refuses None outside Development, and a chart sets no environment, so Production is what runs (§15.4)." $security) }}
{{- end }}
{{- $userName := include "commerce.require" (list $mail.userName "mail.userName is required when mail.enabled: the host refuses anonymous submission outside Development, and the password beside it is mail.passwordSecretRef (§15.4).") }}
Mail__Host: {{ $host | quote }}
Mail__Port: {{ $port | quote }}
Mail__From: {{ $from | quote }}
Mail__Security: {{ $security | quote }}
Mail__UserName: {{ $userName | quote }}
{{- end }}
{{- if (.Values.objectStore).enabled }}
{{- /* ADR-106's store, whose address, bucket and region are Config and whose credential
pair is commerce.env's (§15.4). HTTPS unconditionally, as `commerce.requireUrl` argues. The
BFF's endpoint is the one a client reaches, since it signs the URL the client PUTs to, and
Catalog's is the one that service reaches; only Catalog stores the public base a stored URL
is built from (§10.8), and the host refuses a base that is not https, loopback excepted, when it starts. */}}
{{- $store := .Values.objectStore }}
ProductImages__Store__Endpoint: {{ include "commerce.requireUrl" (list $store.endpoint "objectStore.endpoint is required when objectStore.enabled: the host binds ProductImages:Store and ValidateOnStart refuses to boot without it (§15.4).") | quote }}
ProductImages__Store__Bucket: {{ include "commerce.require" (list $store.bucket "objectStore.bucket is required when objectStore.enabled (§15.4).") | quote }}
ProductImages__Store__Region: {{ include "commerce.require" (list $store.region "objectStore.region is required when objectStore.enabled: a signature is scoped to it (§15.4).") | quote }}
{{- /* A bool option the host binds and validates on start. The chart is stricter than the binder, which also
reads other spellings of true and false: a value such as 1, yes or on renders and crashes the pod, so only the two
are let by. */}}
{{- if not (or (kindIs "bool" $store.pathStyle) (kindIs "invalid" $store.pathStyle) (has $store.pathStyle (list "true" "false"))) }}
{{- fail (printf "objectStore.pathStyle is %v, which is not a boolean. The host binds it to a bool, so a value such as 1, yes or on renders cleanly and the pod never starts; this chart accepts true or false only (§15.4)." $store.pathStyle) }}
{{- end }}
ProductImages__Store__PathStyle: {{ $store.pathStyle | default false | toString | quote }}
{{- if eq .Chart.Name "catalog" }}
ProductImages__Store__PublicBaseUrl: {{ include "commerce.requireUrl" (list $store.publicBaseUrl "objectStore.publicBaseUrl is required on the catalog chart when objectStore.enabled: it is where products/ is served from, and the stored image URL is built from it (§10.8).") | quote }}
{{- end }}
{{- end }}
{{- if (.Values.contactSource).enabled }}
{{- /* ADR-052's contact read. Keycloak can serve its admin API on a hostname of its
own, so the base is not derived from the authority; HTTPS unconditionally, as
`commerce.requireUrl` argues. The realm is the authority's, because the realm
whose users are read is the one that issues this host's token. */}}
ContactSource__BaseUrl: {{ include "commerce.requireUrl" (list .Values.contactSource.baseUrl "contactSource.baseUrl is required when contactSource.enabled: the worker reads it eagerly (ADR-052) and does not start without it (§15.4).") | quote }}
{{- $realm := include "commerce.require" (list .Values.contactSource.realm "contactSource.realm is required when contactSource.enabled: the worker builds its admin path from it at start (§15.4).") }}
{{- if or (not (regexMatch "^[A-Za-z0-9._-]+$" $realm)) (eq $realm ".") (eq $realm "..") }}
{{- fail (printf "contactSource.realm is %q, which is not a realm name this chart will accept: one path segment of letters, digits, dots, hyphens and underscores (§15.4)." $realm) }}
{{- end }}
{{- $issuer := base (trimSuffix "/" (toString .Values.identity.authority)) }}
{{- if ne $realm $issuer }}
{{- fail (printf "contactSource.realm is %q and identity.authority names the realm %q: the realm whose users this host reads is the one that issues its token (ADR-052), so the two must agree." $realm $issuer) }}
{{- end }}
ContactSource__Realm: {{ $realm | quote }}
{{- end }}
{{- if (.Values.delivery).enabled }}
{{- /* ADR-052's give-up age for a waiting notification; DeliveryOptions owns its range. */}}
Delivery__GiveUpAge: {{ include "commerce.timeSpan" (list .Values.delivery.giveUpAge "delivery.giveUpAge" "delivery.giveUpAge is required when delivery.enabled: ADR-052's give-up age is a value the deployment is given, and DeliveryOptions refuses to boot without it." "DeliveryOptions binds it at start (ADR-052).") | quote }}
{{- end }}
{{- /* ADR-117's app list (§10.10), which a registration's app id and platform
must match. The host refuses an app id named twice at start; empty is a
deployment that sends no push, and the BFF then refuses every registration. */}}
{{- range $i, $app := (.Values.push).apps }}
{{- if not (has (toString $app.platform) (list "fcm" "apns")) }}
{{- fail (printf "push.apps[%d].platform is %q, and a token belongs to fcm or apns (ADR-117)." $i (toString $app.platform)) }}
{{- end }}
Push__Apps__{{ $i }}__AppId: {{ include "commerce.require" (list $app.appId (printf "push.apps[%d].appId is required: it is the bundle id or package name a registration names (ADR-117)." $i)) | quote }}
Push__Apps__{{ $i }}__Platform: {{ $app.platform | quote }}
{{- end }}
{{- /* The providers the worker registers for the platforms its apps name, each
over HTTPS unconditionally, as `commerce.requireUrl` argues; its signing key is
commerce.env's. A chart with no provider block, the BFF's, renders none. */}}
{{- $providers := splitList "," (include "commerce.pushProviders" .) }}
{{- if has "fcm" $providers }}
Fcm__BaseUrl: {{ include "commerce.requireUrl" (list .Values.fcm.baseUrl "fcm.baseUrl is required while push.apps names an fcm app: the worker registers FCM's provider and validates FcmOptions at start (§15.4).") | quote }}
Fcm__TokenUri: {{ include "commerce.requireUrl" (list .Values.fcm.tokenUri "fcm.tokenUri is required while push.apps names an fcm app: the signed assertion is exchanged there (§15.4).") | quote }}
Fcm__ProjectId: {{ include "commerce.pushWord" (list .Values.fcm.projectId "fcm.projectId") | quote }}
Fcm__ClientEmail: {{ include "commerce.pushWord" (list .Values.fcm.clientEmail "fcm.clientEmail") | quote }}
{{- end }}
{{- if has "apns" $providers }}
Apns__BaseUrl: {{ include "commerce.requireUrl" (list .Values.apns.baseUrl "apns.baseUrl is required while push.apps names an apns app: the worker registers APNs' provider and validates ApnsOptions at start (§15.4).") | quote }}
Apns__TeamId: {{ include "commerce.pushWord" (list .Values.apns.teamId "apns.teamId") | quote }}
Apns__KeyId: {{ include "commerce.pushWord" (list .Values.apns.keyId "apns.keyId") | quote }}
{{- end }}
{{- /* ADR-079's opt-out, by connection name: each one listed is a hop the
deployer accepts in plaintext. Empty or absent, as each chart ships it, the
host refuses each one that is not encrypted and verified. */}}
{{- range $i, $name := (.Values.transport).plaintext }}
{{- if not (regexMatch "^[A-Za-z][A-Za-z0-9]*$" (toString $name)) }}
{{- fail (printf "transport.plaintext[%d] is %q, which names no connection string: a key such as RabbitMq or RedisCache (ADR-079)." $i (toString $name)) }}
{{- end }}
Transport__Plaintext__{{ $i }}: {{ $name | quote }}
{{- end }}
{{- end -}}

{{- /* A .NET TimeSpan as this chart accepts one, `[d.]hh:mm[:ss[.fffffff]]` with hours
under 24 and minutes and seconds under 60, held once for every setting that
binds one. Hours under 24 because the binder reads `72:00:00` as seventy-two
days, silently. */}}
{{- define "commerce.timeSpanPattern" -}}
^([0-9]+\.)?([01]?[0-9]|2[0-3]):[0-5][0-9](:[0-5][0-9](\.[0-9]{1,7})?)?$
{{- end -}}

{{- /* A TimeSpan setting, required and shaped, because `3 days` renders and fails
binding in the new pod. The range is the host's, whose options type owns its
bounds. Arguments: the value, its values path, the message for a blank one,
and the sentence naming what binds it. */}}
{{- define "commerce.timeSpan" -}}
{{- $value := include "commerce.require" (list (index . 0) (index . 2)) -}}
{{- if not (regexMatch (include "commerce.timeSpanPattern" .) $value) -}}
{{- fail (printf "%s is %q, which is not a TimeSpan this chart will accept: [d.]hh:mm[:ss], as in 3.00:00:00 for three days. %s" (index . 1) $value (index . 3)) -}}
{{- end -}}
{{- $value -}}
{{- end -}}

{{- /* The secret half of the same table. A variable joins when a host's code reads
it, and a secret is referenced, never rendered: External Secrets Operator
owns the Secret objects (§15.4), and a rendered one would put a password
into `helm get values`. */}}
{{- define "commerce.env" -}}
{{- /* A capability is a fact about the code, not an environment setting: Catalog
always resolves its database and the BFF always binds ServiceIdentityOptions,
so disabling either renders and the pod does not start. Helm has no
immutable value, so a chart carrying a capability's settings may not disable
it. */}}
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
{{- $jurisdictionSet := false }}
{{- range $key, $value := omit (.Values.jurisdiction | default dict) "enabled" }}
{{- if $value }}
{{- $jurisdictionSet = true }}
{{- end }}
{{- end }}
{{- if and $jurisdictionSet (not (.Values.jurisdiction).enabled) }}
{{- fail "jurisdiction.enabled is false but a jurisdiction setting is set. The host's jurisdiction options are validated at start (ADR-053), so this renders cleanly and the host does not start." }}
{{- end }}
{{- if and (or (.Values.privacy).responders (.Values.privacy).completionSlo) (not (.Values.privacy).enabled) }}
{{- fail "privacy.enabled is false but a privacy setting is set. PrivacyOptions is validated at start (ADR-092), so this renders cleanly and the host does not start." }}
{{- end }}
{{- if and (.Values.fulfilment).giveUpAge (not (.Values.fulfilment).enabled) }}
{{- fail "fulfilment.enabled is false but fulfilment.giveUpAge is set. FulfilmentOptions is validated at start (ADR-052), so this renders cleanly and the host does not start." }}
{{- end }}
{{- if and (or (.Values.mail).host (.Values.mail).port (.Values.mail).from (.Values.mail).security (.Values.mail).userName (.Values.mail).passwordSecretRef) (not (.Values.mail).enabled) }}
{{- fail "mail.enabled is false but a mail setting is set. The worker reads the relay's keys eagerly (§15.4), so this renders cleanly and the host does not start. A capability is a fact about the code, not an environment setting." }}
{{- end }}
{{- if and (or (.Values.objectStore).endpoint (.Values.objectStore).bucket (.Values.objectStore).region (.Values.objectStore).publicBaseUrl (.Values.objectStore).secretRef) (not (.Values.objectStore).enabled) }}
{{- fail "objectStore.enabled is false but an objectStore setting is set. The host validates ProductImages:Store when it starts (§15.4), so this renders cleanly and the host does not start. A capability is a fact about the code, not an environment setting." }}
{{- end }}
{{- if and (.Values.objectStore).enabled (not (has .Chart.Name (list "web-bff" "catalog"))) }}
{{- fail (printf "objectStore.enabled is true on the %s chart, and only web-bff (which signs an upload) and catalog (which attaches it) touch the store (ADR-106). This would mount a credential pair into a pod that never reads it." .Chart.Name) }}
{{- end }}
{{- if and (.Values.objectStore).publicBaseUrl (ne .Chart.Name "catalog") }}
{{- fail "objectStore.publicBaseUrl is set on a chart that is not catalog. Only the service that stores a published image's URL builds it from a base (§10.8); on the BFF it would be a value nothing reads." }}
{{- end }}
{{- if and (or (.Values.contactSource).baseUrl (.Values.contactSource).realm) (not (.Values.contactSource).enabled) }}
{{- fail "contactSource.enabled is false but a contactSource setting is set. The worker reads ADR-052's contact source at startup, so this renders cleanly and the host does not start." }}
{{- end }}
{{- if and (.Values.delivery).giveUpAge (not (.Values.delivery).enabled) }}
{{- fail "delivery.enabled is false but delivery.giveUpAge is set. DeliveryOptions is validated at start (ADR-052), so this renders cleanly and the host does not start." }}
{{- end }}
{{- /* The other direction moves a credential: Helm accepts values a chart never
declares, so `paymentProvider.enabled` on another chart would mount Payments'
provider Secret into a pod that never reads it. These name the owning
charts, so a further chart growing one is a design change made here. */}}
{{- if and (.Values.paymentProvider).enabled (ne .Chart.Name "payments") }}
{{- fail (printf "paymentProvider.enabled is true on the %s chart, and only payments registers a provider (§3.2). This would mount the provider's Secret into a pod that never reads it — a credential crossing a service boundary, which no value in an environment file may do." .Chart.Name) }}
{{- end }}
{{- if and (.Values.carrier).enabled (ne .Chart.Name "shipping") }}
{{- fail (printf "carrier.enabled is true on the %s chart, and only shipping books with a carrier (§3.2). This would mount the carrier's Secret into a pod that never reads it — a credential crossing a service boundary, which no value in an environment file may do." .Chart.Name) }}
{{- end }}
{{- if and (.Values.privacy).enabled (ne .Chart.Name "privacy") }}
{{- fail (printf "privacy.enabled is true on the %s chart, and only privacy tracks an erasure request (ADR-092). This would hand a service the holder set that decides when a person's data counts as gone." .Chart.Name) }}
{{- end }}
{{- if and (or .Values.fcm .Values.apns) (ne .Chart.Name "notifications") }}
{{- fail (printf "fcm or apns is set on the %s chart, and only notifications sends a push (ADR-118). This would mount a provider's signing key into a pod that never reads it — a credential crossing a service boundary, which no value in an environment file may do." .Chart.Name) }}
{{- end }}
{{- if and (.Values.mail).enabled (ne .Chart.Name "notifications") }}
{{- fail (printf "mail.enabled is true on the %s chart, and only notifications submits to a relay (§3.2). This would mount the relay's Secret into a pod that never reads it — a credential crossing a service boundary, which no value in an environment file may do." .Chart.Name) }}
{{- end }}
{{- if and .Values.identity.clientCredentials (not (has .Chart.Name (list "web-bff" "shipping" "notifications"))) }}
{{- fail (printf "identity.clientCredentials is true on the %s chart, and the hosts that call out under a grant of their own are the BFF (§9.7, ADR-017) and the workers ADR-052 gives a read. This would mount one of their client secrets into a pod that never presents it — a credential crossing a service boundary, which no value in an environment file may do." .Chart.Name) }}
{{- end }}
{{- if .Values.database.enabled }}
{{- /* The runtime connection string (DML only) — §7.1's split identity. The migrator
key is the other half, mounted into the migration Job and nowhere else. */}}
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
{{- /* §8.1's two connections, both required because AddRedisConnections reads both
eagerly. Secrets, since each string carries a credential (§8.1), and two
references because the instances differ in eviction policy, which is why
the guard above refuses one key for both. */}}
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
{{- if (.Values.objectStore).enabled }}
{{- /* The pair is a Secret, one per host and never shared: the BFF's writes uploads/ and the
product service's reads it and writes products/, and neither lists or deletes (§10.8). */}}
- name: ProductImages__Store__AccessKey
  valueFrom:
    secretKeyRef:
      name: {{ include "commerce.require" (list (.Values.objectStore.secretRef).name "objectStore.secretRef.name is required when objectStore.enabled. The pair is a reference, never a value (§15.3).") | quote }}
      key: {{ include "commerce.require" (list (.Values.objectStore.secretRef).accessKeyKey "objectStore.secretRef.accessKeyKey is required when objectStore.enabled.") | quote }}
- name: ProductImages__Store__SecretKey
  valueFrom:
    secretKeyRef:
      name: {{ include "commerce.require" (list (.Values.objectStore.secretRef).name "objectStore.secretRef.name is required when objectStore.enabled.") | quote }}
      key: {{ include "commerce.require" (list (.Values.objectStore.secretRef).secretKeyKey "objectStore.secretRef.secretKeyKey is required when objectStore.enabled.") | quote }}
{{- end }}
{{- if (.Values.mail).enabled }}
- name: Mail__Password
  valueFrom:
    secretKeyRef:
      name: {{ include "commerce.require" (list (.Values.mail.passwordSecretRef).name "mail.passwordSecretRef.name is required when mail.enabled. The password is a reference, never a value (§15.3).") | quote }}
      key: {{ include "commerce.require" (list (.Values.mail.passwordSecretRef).key "mail.passwordSecretRef.key is required when mail.enabled.") | quote }}
{{- end }}
{{- $providers := splitList "," (include "commerce.pushProviders" .) }}
{{- if has "fcm" $providers }}
- name: Fcm__PrivateKey
  valueFrom:
    secretKeyRef:
      name: {{ include "commerce.require" (list (.Values.fcm.privateKeySecretRef).name "fcm.privateKeySecretRef.name is required while push.apps names an fcm app. The key is a reference, never a value (§15.3).") | quote }}
      key: {{ include "commerce.require" (list (.Values.fcm.privateKeySecretRef).key "fcm.privateKeySecretRef.key is required while push.apps names an fcm app.") | quote }}
{{- end }}
{{- if has "apns" $providers }}
- name: Apns__PrivateKey
  valueFrom:
    secretKeyRef:
      name: {{ include "commerce.require" (list (.Values.apns.privateKeySecretRef).name "apns.privateKeySecretRef.name is required while push.apps names an apns app. The key is a reference, never a value (§15.3).") | quote }}
      key: {{ include "commerce.require" (list (.Values.apns.privateKeySecretRef).key "apns.privateKeySecretRef.key is required while push.apps names an apns app.") | quote }}
{{- end }}
{{- end -}}

{{- /* The push providers this chart calls: each platform its apps name for which
it carries a provider block, comma-joined, empty on a chart with none (ADR-118). */}}
{{- define "commerce.pushProviders" -}}
{{- $platforms := list -}}
{{- range (.Values.push).apps }}{{- $platforms = append $platforms (toString .platform) -}}{{- end -}}
{{- $out := list -}}
{{- if and .Values.fcm (has "fcm" $platforms) }}{{- $out = append $out "fcm" -}}{{- end -}}
{{- if and .Values.apns (has "apns" $platforms) }}{{- $out = append $out "apns" -}}{{- end -}}
{{- join "," $out -}}
{{- end -}}

{{- /* A provider value that lands in a URL, a claim or a header, so one printable
word: list (value, key). */}}
{{- define "commerce.pushWord" -}}
{{- $value := include "commerce.require" (list (index . 0) (printf "%s is required while push.apps names its platform: the worker validates it at start (§15.4)." (index . 1))) -}}
{{- if not (regexMatch "^[!-~]+$" $value) }}
{{- fail (printf "%s is %q, which is not one printable ASCII word: it lands in a URL, a claim or a header (§15.4)." (index . 1) $value) }}
{{- end -}}
{{- $value -}}
{{- end -}}
