{{- define "commerce.configmap" -}}
apiVersion: v1
kind: ConfigMap
metadata:
  {{- /* Per instance, so the canary release owns its own (§15.3 ownership). */}}
  name: {{ include "commerce.instanceName" . }}-config
  labels:
    {{- include "commerce.labels" . | nindent 4 }}
data:
  {{- include "commerce.config" . | nindent 2 }}
  {{- if .Values.suspendable }}
  {{- /* The kubelet swaps a mounted ConfigMap through a symlink, which inotify
  misses; polling sees it (ADR-112). */}}
  DOTNET_USE_POLLING_FILE_WATCHER: "true"
  {{- end }}
{{- end -}}
