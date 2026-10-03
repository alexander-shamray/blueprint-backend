{{- /* Rendered only where something dials this workload by name, and never on
the canary release: §15.5 splits traffic by both tracks answering to the one
Service the stable release owns. A worker writes `service.enabled: false`
rather than omitting it (§15.3). */}}
{{- define "commerce.service" -}}
{{- if and .Values.service.enabled (not .Values.canary.enabled) -}}
apiVersion: v1
kind: Service
metadata:
  name: {{ include "commerce.name" . }}
  labels:
    {{- include "commerce.labels" . | nindent 4 }}
spec:
  type: ClusterIP
  selector:
    {{- include "commerce.selectorLabels" . | nindent 4 }}
  ports:
    {{- /* The Service port is the container port, not a remapping (§10.2, §9.7). */}}
    {{- range .Values.ports }}
    - name: {{ .name }}
      port: {{ .containerPort }}
      targetPort: {{ .name }}
      protocol: TCP
    {{- end }}
{{- end -}}
{{- end -}}
