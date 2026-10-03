{{- /* Not on the canary release: its served weight is the replica ratio, so an
autoscaler on either track would move the blast radius under the analysis
judging it. The rollout still sets `autoscaling.enabled=false` on the canary,
because that value also decides whether the Deployment renders `replicas`. */}}
{{- define "commerce.hpa" -}}
{{- if and .Values.autoscaling.enabled (not .Values.canary.enabled) -}}
apiVersion: autoscaling/v2
kind: HorizontalPodAutoscaler
metadata:
  name: {{ include "commerce.name" . }}
  labels:
    {{- include "commerce.labels" . | nindent 4 }}
spec:
  scaleTargetRef:
    apiVersion: apps/v1
    kind: Deployment
    name: {{ include "commerce.name" . }}
  minReplicas: {{ .Values.autoscaling.minReplicas }}
  maxReplicas: {{ .Values.autoscaling.maxReplicas }}
  metrics:
    {{- /* Utilisation is a percentage of the CPU request (§15.3). */}}
    - type: Resource
      resource:
        name: cpu
        target:
          type: Utilization
          averageUtilization: {{ .Values.autoscaling.targetCPUUtilizationPercentage }}
{{- end -}}
{{- end -}}

{{- /* Stable-only, because a canary's budget would take the same name. Its
selector matches both tracks, so it protects the canary's pods too but bounds
only the total, not the canary's weight during §15.5's ladder: ADR-022 records
that residual. */}}
{{- define "commerce.pdb" -}}
{{- if and .Values.podDisruptionBudget.enabled (not .Values.canary.enabled) -}}
apiVersion: policy/v1
kind: PodDisruptionBudget
metadata:
  name: {{ include "commerce.name" . }}
  labels:
    {{- include "commerce.labels" . | nindent 4 }}
spec:
  minAvailable: {{ .Values.podDisruptionBudget.minAvailable }}
  selector:
    matchLabels:
      {{- include "commerce.selectorLabels" . | nindent 6 }}
{{- end -}}
{{- end -}}
