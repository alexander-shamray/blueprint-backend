{{- /* The platform's one route in, and only the gateway has it (§10.1). The
same key tells the host it sits behind a proxy (§15.4), and TLS terminates
here, so every hop past this object is plain http. */}}
{{- define "commerce.ingress" -}}
{{- /* Never on the canary release, which is reached through the stable
release's Service. Suppressed before the guards below, since a canary inherits
`ingress.enabled` and would otherwise need a TLS secret for an object it must
not create. */}}
{{- if and .Values.ingress.enabled (not .Values.canary.enabled) -}}
{{- /* An Ingress whose backend does not exist renders, installs and reports success,
and answers every request with a 503 from the controller. `service.enabled` is
what creates that backend, so the two keys are not independent — and the
combination is exactly what a copied values file produces when someone turns a
worker's Service off and leaves the edge's Ingress on. */}}
{{- if not .Values.service.enabled }}
{{- fail "ingress.enabled requires service.enabled: the Ingress backend is this workload's Service, so with no Service the release installs cleanly and the controller answers 503 for every request (§15.3)." }}
{{- end }}
{{- /* Required, because TLS terminates here (§10.1): the gateway's scheme
rewrite, ADR-020's compression decision and the BFF's plain `http://` hop all
rest on it, and without it a valid plaintext Ingress renders. */}}
{{- if not .Values.ingress.tls }}
{{- fail "ingress.tls is required when ingress.enabled: TLS terminates at the Ingress (§10.1), and every hop past it is plain http on that premise. Rendering without it publishes the platform's only external route in cleartext." }}
{{- end }}
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: {{ include "commerce.name" . }}
  labels:
    {{- include "commerce.labels" . | nindent 4 }}
  {{- with .Values.ingress.annotations }}
  annotations:
    {{- toYaml . | nindent 4 }}
  {{- end }}
spec:
  ingressClassName: {{ include "commerce.require" (list .Values.ingress.className "ingress.className is required when ingress.enabled: an Ingress with no class is picked up by whichever controller claims the default, which is not a deployment decision to leave to a cluster.") | quote }}
  {{- $host := include "commerce.require" (list .Values.ingress.host "ingress.host is required when ingress.enabled.") }}
  {{- /* Unconditional: the guard above refuses to render without it. */}}
  tls:
    - hosts: [{{ $host | quote }}]
      secretName: {{ include "commerce.require" (list .Values.ingress.tls.secretName "ingress.tls.secretName is required when ingress.enabled.") | quote }}
  rules:
    - host: {{ $host | quote }}
      http:
        paths:
          {{- /* One rule to the whole gateway, not a rule per service. §10.2's route
          file is the platform's routing table and it lives in the gateway's
          appsettings.json; splitting it across Ingress paths would create a
          second one, and the two would disagree the first time a route moved. */}}
          - path: /
            pathType: Prefix
            backend:
              service:
                name: {{ include "commerce.name" . }}
                port:
                  name: http
{{- end -}}
{{- end -}}
