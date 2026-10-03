{{- /* §7.4's migration hook, for ADR-007's reasons, and idempotent, so a
config-only deploy runs it again safely (§15.1). A chart that owns no database
carries no `migrate-job.yaml`. */}}
{{- define "commerce.migrationJob" -}}
{{- /* Required rather than an `if`: only a chart that owns a database includes
this template, and an opt-out here would roll application pods against an
unmigrated schema without an error. */}}
{{- $migrator := include "commerce.require" (list .Values.image.migrator "image.migrator is required in a chart that carries templates/migrate-job.yaml: clearing it would drop the hook and roll application pods against an unmigrated schema (§7.4, ADR-007).") -}}
{{- if not .Values.database.enabled }}
{{- fail "image.migrator is set but database.enabled is false. A migrator with no database is incoherent — the Job would mount a connection string the service is not configured to use (§7.1)." }}
{{- end }}
{{- $tag := include "commerce.tag" . -}}
apiVersion: batch/v1
kind: Job
metadata:
  {{- /* Validated whole rather than truncated: the name becomes every pod's
  `job-name` label, and a cut can end on a character the API server refuses
  mid-upgrade, or give two tags one Job. */}}
  {{- $jobName := printf "%s-migrate-%s" (include "commerce.name" .) $tag }}
  {{- if gt (len $jobName) 63 }}
  {{- fail (printf "the migration Job would be named %q, which is %d characters. Kubernetes copies that onto every pod as the `job-name` label and a label value may not exceed 63, so this is refused here rather than by the API server mid-upgrade. Shorten image.tag: the workload name and `-migrate-` cost %d, leaving %d." $jobName (len $jobName) (sub (len $jobName) (len $tag)) (sub 63 (sub (len $jobName) (len $tag)))) }}
  {{- end }}
  name: {{ $jobName }}
  labels:
    {{- include "commerce.labels" . | nindent 4 }}
  annotations:
    "helm.sh/hook": pre-install,pre-upgrade
    "helm.sh/hook-weight": "-5"
    {{- /* `hook-succeeded` too, because the name embeds the tag. A failed Job
    stays, and no TTL is set, because `migration-failure.md` starts from it. */}}
    "helm.sh/hook-delete-policy": before-hook-creation,hook-succeeded
spec:
  backoffLimit: 2
  template:
    metadata:
      labels:
        {{/* Not `commerce.labels`, for the reason `commerce.migrationPodLabels` gives. */}}
        {{- include "commerce.migrationPodLabels" . | nindent 8 }}
    spec:
      restartPolicy: Never
      {{- /* As on the Deployment, and more so: this pod holds the migrator
      credential (§7.1), the platform's one identity with DDL rights, and
      connects to nothing but its database. */}}
      automountServiceAccountToken: false
      securityContext:
        runAsNonRoot: true
      containers:
        - name: migrate
          image: "{{ include "commerce.require" (list .Values.image.registry "image.registry is required: cleared, the hook image has no host and the migration never runs (§7.4).") }}/{{ $migrator }}:{{ $tag }}"
          imagePullPolicy: {{ .Values.image.pullPolicy }}
          securityContext:
            allowPrivilegeEscalation: false
            capabilities:
              drop: [ALL]
          env:
            {{- /* The migrator identity (DDL), not the runtime one: §7.1's split,
            and this Secret is mounted into no API pod. */}}
            - name: ConnectionStrings__{{ .Values.database.connectionName }}Migrator
              valueFrom:
                secretKeyRef:
                  name: {{ include "commerce.require" (list .Values.database.migratorSecretRef.name "database.migratorSecretRef.name is required whenever image.migrator is set.") | quote }}
                  key: {{ include "commerce.require" (list .Values.database.migratorSecretRef.key "database.migratorSecretRef.key is required whenever image.migrator is set.") | quote }}
          resources:
            {{- toYaml .Values.migrationJob.resources | nindent 12 }}
{{- end -}}
