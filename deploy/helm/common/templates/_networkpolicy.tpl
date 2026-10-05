{{- /* A list of NetworkPolicy peers the deployment states, required: a rule with no
peer admits every address on its port, the opposite of a fence. An ipBlock of
every address is refused for the same reason (ADR-065). */}}
{{- define "commerce.networkPolicyPeers" -}}
{{- $peers := index . 0 -}}
{{- $key := index . 1 -}}
{{- $why := index . 2 -}}
{{- if not $peers }}
{{- fail (printf "networkPolicy.%s is required: %s A rule with no peer admits every address on its port, so the chart refuses to render one (ADR-065)." $key $why) }}
{{- end }}
{{- range $peers }}
{{- if has ((.ipBlock).cidr | default "" | toString) (list "0.0.0.0/0" "::/0") }}
{{- fail (printf "networkPolicy.%s names %s, which admits every address: state the peer's own range (ADR-065)." $key .ipBlock.cidr) }}
{{- end }}
{{- end }}
{{- toYaml $peers }}
{{- end -}}

{{- /* One egress rule: the peers, and one TCP port. */}}
{{- define "commerce.networkPolicyEgress" -}}
{{- $peers := index . 0 -}}
{{- $key := index . 1 -}}
{{- $why := index . 2 -}}
{{- $port := index . 3 -}}
- to:
    {{- include "commerce.networkPolicyPeers" (list $peers $key $why) | nindent 4 }}
  ports:
    - port: {{ include "commerce.require" (list $port (printf "networkPolicy's port for %s is required (ADR-065)." $key)) }}
      protocol: TCP
{{- end -}}

{{- /* The port an address dials, read from the address its host is given rather
than stated twice: its own port, or its scheme's. A policy matches the pod's
port after a Service translates it, so a peer's stated port wins (ADR-065). */}}
{{- define "commerce.urlPort" -}}
{{- $url := . | default "" | toString -}}
{{- $port := regexFind ":[0-9]+$" (regexFind "^[a-z]+://[^/]+" $url) -}}
{{- if $port }}{{ trimPrefix ":" $port }}{{ else if hasPrefix "https://" $url }}443{{ else }}80{{ end -}}
{{- end -}}

{{- /* ADR-065's fence, on both tracks: each release selects its own track's pods
and carries the same rules, so the canary is judged inside the fence the stable
track runs in (ADR-022). Ingress and egress are denied but for the edges below;
the kubelet's probes come from the node, which a policy never blocks. */}}
{{- define "commerce.networkPolicy" -}}
{{- $np := .Values.networkPolicy | default dict -}}
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: {{ include "commerce.instanceName" . }}
  labels:
    {{- include "commerce.labels" . | nindent 4 }}
    app.kubernetes.io/track: {{ include "commerce.track" . }}
spec:
  podSelector:
    matchLabels:
      {{- include "commerce.deploymentSelectorLabels" . | nindent 6 }}
  policyTypes: [Ingress, Egress]
  {{- if or .Values.ingress.enabled $np.ingressFrom }}
  ingress:
    {{- if .Values.ingress.enabled }}
    {{- /* The edge's one route in, from wherever the cluster runs its controller. */}}
    - from:
        {{- include "commerce.networkPolicyPeers" (list ($np.ingressController).from "ingressController.from" "the gateway's Ingress is served by a controller this policy must admit.") | nindent 8 }}
      ports:
        - port: http
          protocol: TCP
    {{- end }}
    {{- range $np.ingressFrom }}
    - from:
        - podSelector:
            matchLabels:
              app.kubernetes.io/name: {{ include "commerce.require" (list .workload "networkPolicy.ingressFrom[].workload is required (ADR-065).") }}
              app.kubernetes.io/part-of: commerce
      ports:
        - port: {{ include "commerce.require" (list .port "networkPolicy.ingressFrom[].port is required (ADR-065).") }}
          protocol: TCP
    {{- end }}
  {{- else }}
  ingress: []
  {{- end }}
  egress:
    - to:
        {{- include "commerce.networkPolicyPeers" (list ($np.dns).to "dns.to" "every pod resolves its peers' names.") | nindent 8 }}
      ports:
        - port: 53
          protocol: UDP
        - port: 53
          protocol: TCP
    {{- /* Every host exports its telemetry (§13.2) and validates tokens against the
    identity provider (§11.2), whether or not it holds a grant of its own. */}}
    {{- include "commerce.networkPolicyEgress" (list ($np.telemetry).to "telemetry.to" "every host exports to the OTLP endpoint (§13.2)." (($np.telemetry).port | default (include "commerce.urlPort" .Values.observability.otlpEndpoint))) | nindent 4 }}
    {{- include "commerce.networkPolicyEgress" (list ($np.identity).to "identity.to" "every host validates tokens against the identity provider (§11.2)." (($np.identity).port | default (include "commerce.urlPort" .Values.identity.authority))) | nindent 4 }}
    {{- if and (.Values.contactSource).enabled (not ($np.identity).port) (ne (include "commerce.urlPort" .Values.contactSource.baseUrl) (include "commerce.urlPort" .Values.identity.authority)) }}
    {{- include "commerce.networkPolicyEgress" (list ($np.identity).to "identity.to" "contactSource reads Keycloak's admin API (ADR-052)." (include "commerce.urlPort" .Values.contactSource.baseUrl)) | nindent 4 }}
    {{- end }}
    {{- if .Values.database.enabled }}
    {{- include "commerce.networkPolicyEgress" (list ($np.database).to "database.to" "database.enabled says this host and its migrator reach a database." ($np.database).port) | nindent 4 }}
    {{- end }}
    {{- if .Values.redis.enabled }}
    {{- include "commerce.networkPolicyEgress" (list ($np.redis).to "redis.to" "redis.enabled says this host reaches both Redis instances (§8.1)." ($np.redis).port) | nindent 4 }}
    {{- end }}
    {{- if .Values.broker.enabled }}
    {{- include "commerce.networkPolicyEgress" (list ($np.broker).to "broker.to" "broker.enabled says this host reaches the broker (§9)." ($np.broker).port) | nindent 4 }}
    {{- end }}
    {{- if (.Values.paymentProvider).enabled }}
    {{- include "commerce.networkPolicyEgress" (list ($np.paymentProvider).to "paymentProvider.to" "paymentProvider.enabled says this host calls the provider (§3.2)." (($np.paymentProvider).port | default (include "commerce.urlPort" .Values.paymentProvider.baseUrl))) | nindent 4 }}
    {{- end }}
    {{- if (.Values.carrier).enabled }}
    {{- include "commerce.networkPolicyEgress" (list ($np.carrier).to "carrier.to" "carrier.enabled says this host calls the carrier (§3.2)." (($np.carrier).port | default (include "commerce.urlPort" .Values.carrier.baseUrl))) | nindent 4 }}
    {{- end }}
    {{- if (.Values.mail).enabled }}
    {{- include "commerce.networkPolicyEgress" (list ($np.mail).to "mail.to" "mail.enabled says this host submits to the relay." (($np.mail).port | default .Values.mail.port | toString)) | nindent 4 }}
    {{- end }}
    {{- range $np.egressTo }}
    - to:
        - podSelector:
            matchLabels:
              app.kubernetes.io/name: {{ include "commerce.require" (list .workload "networkPolicy.egressTo[].workload is required (ADR-065).") }}
              app.kubernetes.io/part-of: commerce
      ports:
        - port: {{ include "commerce.require" (list .port "networkPolicy.egressTo[].port is required (ADR-065).") }}
          protocol: TCP
    {{- end }}
{{- end -}}
