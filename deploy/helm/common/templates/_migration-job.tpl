{{- /*
§7.4's migration Job, run by Helm as a hook before anything else in the release
moves. ADR-007 is the decision behind it: never `Database.Migrate()` at
application startup, because three replicas race to apply the same migration, a
rolling deploy runs old code against a new schema, and the runtime identity
would need DDL permissions it must not have.

The hook weight is negative so this runs ahead of any other hook a chart grows,
and `before-hook-creation` deletes the previous Job of the same name rather
than leaving a graveyard of completed ones. Both are §7.4's.

**Idempotent, not merely correct once** (§15.1). A config-only deploy goes
through the same `helm upgrade` and therefore the same hook with the same
migrator image, and `Database.Migrate()` applies nothing when the history table
is already current — which is what makes re-running it on every deploy safe.

Rendered only where `image.migrator` is set — and the gateway and the BFF do
not include this template at all, because they own no database (§10.1, §4.2).
Two ways of saying one thing, deliberately: §15.3 argues the gateway's chart is
not a service chart with the database parts deleted, so the absence is
structural, and the guard here is what catches a chart that grows the template
before it grows the value. smoke.sh asserts the two agree, because either half
on its own is a claim nothing tests.
*/}}
{{- define "commerce.migrationJob" -}}
{{- /*
REQUIRED, not `if`. A chart that includes this template is a chart that owns a
database, and `image.migrator` was acting as an opt-out: clearing it made the
Job vanish and let the release roll application pods against an unmigrated
schema — silently, because a template that renders nothing renders no error.

The gateway and the BFF do not reach this line at all; they carry no
`migrate-job.yaml`. That is the structural half, and this is the other: the
only charts that get here are the ones that must run a migration, so the image
is a requirement rather than a switch. `smoke.sh` asserts the two halves agree.
*/}}
{{- $migrator := include "commerce.require" (list .Values.image.migrator "image.migrator is required in a chart that carries templates/migrate-job.yaml: clearing it would drop the hook and roll application pods against an unmigrated schema (§7.4, ADR-007).") -}}
{{- if not .Values.database.enabled }}
{{- fail "image.migrator is set but database.enabled is false. A migrator with no database is incoherent — the Job would mount a connection string the service is not configured to use (§7.1)." }}
{{- end }}
{{- $tag := include "commerce.tag" . -}}
apiVersion: batch/v1
kind: Job
metadata:
  {{- /*
  VALIDATED, not truncated, and the truncation was a defect of its own.
  Kubernetes stamps `job-name` onto the pods this Job creates and a label value
  may not exceed 63 characters, so the name has a budget. `trunc 63 | trimSuffix
  "-"` looked like the guard and cut in the middle of the tag: 42 `a`s followed
  by `.b` truncates immediately after the dot, `trimSuffix "-"` does not touch a
  trailing `.`, and the API server rejects the hook — after `helm upgrade` has
  started.

  A truncated name is also a name that can collide, which is the quieter half:
  two tags sharing a 63-character prefix would produce one Job.

  So the derived name is checked whole and a tag that does not fit is refused.
  The budget is comfortable for what CI supplies — the longest migrator prefix
  among the charts plus a 40-character commit SHA still fits the 63-character
  ceiling, exercised by smoke.sh's long-tag check — and a tag that overruns it
  is a deploy that must fail rather than one that mangles a name.
  */}}
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
    {{- /*
    `hook-succeeded` as well as `before-hook-creation`, and without it these
    Jobs accumulate for ever.

    `before-hook-creation` deletes the previous resource of the SAME NAME, and
    the name above embeds the tag — so every new SHA is a differently named Job
    and nothing ever collects the last one. A namespace would carry one
    completed migration Job per release it has ever seen. That is not
    theoretical tidiness: the runbook (§13.6) looks for the failed Job, and it
    is looking in a list of every migration that has ever succeeded.

    Failures are deliberately NOT collected. `hook-failed` would delete the one
    artefact `migration-failure.md` needs, so a failed migration stays until
    somebody has read it — which is also why there is no
    `ttlSecondsAfterFinished` here: the TTL controller does not distinguish
    Complete from Failed.
    */}}
    "helm.sh/hook-delete-policy": before-hook-creation,hook-succeeded
spec:
  backoffLimit: 2
  template:
    metadata:
      labels:
        {{/*
        NOT `commerce.labels`, which is what the Service selects on — see
        `commerce.migrationPodLabels`. This pod has a database connection and no
        HTTP listener, and for the length of the hook it was an endpoint of the
        service it was migrating.
        */}}
        {{- include "commerce.migrationPodLabels" . | nindent 8 }}
    spec:
      restartPolicy: Never
      {{- /*
      The same reasoning as the Deployment's, and it bites harder here: this
      pod holds the MIGRATOR credential (§7.1), the one identity in the
      platform with DDL rights. It connects to a database and to nothing else,
      so a cluster credential beside it is pure blast radius.
      */}}
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
            {{- /*
            The MIGRATOR identity (DDL), not the runtime one — §7.1's split.
            This secret is mounted here and into no API pod, which is the whole
            point of there being two: the running service holds a login that
            cannot alter its own schema.
            */}}
            - name: ConnectionStrings__{{ .Values.database.connectionName }}Migrator
              valueFrom:
                secretKeyRef:
                  name: {{ include "commerce.require" (list .Values.database.migratorSecretRef.name "database.migratorSecretRef.name is required whenever image.migrator is set.") | quote }}
                  key: {{ include "commerce.require" (list .Values.database.migratorSecretRef.key "database.migratorSecretRef.key is required whenever image.migrator is set.") | quote }}
          resources:
            {{- toYaml .Values.migrationJob.resources | nindent 12 }}
{{- end -}}
