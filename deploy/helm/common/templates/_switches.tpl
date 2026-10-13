{{- /* ADR-113's runtime switch: the names an operator has switched off, as a
ConfigMap the pod mounts as a file. The host reloads the file, so changing
`switches.off` and upgrading the release changes what the pod does without
restarting it; nothing in the pod template depends on it (see the checksum in
commerce.deployment). Only a chart ADR-104 classes as suspendable renders one,
and every other chart refuses a name, so a service on the order's critical path
cannot be switched by a typo in an overlay. */}}
{{- define "commerce.switches" -}}
{{- $off := ((.Values.switches).off) | default list }}
{{- if and $off (not .Values.suspendable) }}
{{- fail (printf "switches.off is set on the %s chart, which ADR-104 does not class as suspendable: switching part of it off cancels orders or ends checkout, and ADR-113 gives it no switch." .Chart.Name) }}
{{- end }}
{{- if .Values.suspendable }}
apiVersion: v1
kind: ConfigMap
metadata:
  {{- /* Per instance, like the config, so the canary release owns its own. */}}
  name: {{ include "commerce.instanceName" . }}-switches
  labels:
    {{- include "commerce.labels" . | nindent 4 }}
data:
  {{- /* The file's name and shape are what Common.Web's OperatorSwitchFile
  reads: a JSON document with `Switches:Off` as a list of names. */}}
  switches.json: {{ dict "Switches" (dict "Off" $off) | toJson | quote }}
{{- end }}
{{- end -}}
