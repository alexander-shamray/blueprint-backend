{{- define "commerce.deployment" -}}
apiVersion: apps/v1
kind: Deployment
metadata:
  {{- /*
  The INSTANCE name, not the workload name: on the canary release these differ,
  because Helm refuses to render an object another release owns (§15.3). On the
  stable release they are the same string, which is why nothing needed the
  distinction before §15.5's canary did.
  */}}
  name: {{ include "commerce.instanceName" . }}
  labels:
    {{- include "commerce.labels" . | nindent 4 }}
    app.kubernetes.io/track: {{ include "commerce.track" . }}
spec:
  {{- if not .Values.autoscaling.enabled }}
  {{- /*
  Omitted entirely when the HPA is on, which is the opposite of setting it to
  minReplicas. `replicas` is a managed field: with it present, every
  `helm upgrade` writes the chart's value and the HPA writes it back, so a
  config-only deploy (§15.1) scales the service down to the chart default and
  the autoscaler climbs out again over the following minutes. Absent, the
  field is left to whoever owns it.
  */}}
  replicas: {{ .Values.replicaCount }}
  {{- end }}
  selector:
    {{- /*
    The shared selector plus the track: two Deployments sharing a selector
    count each other's pods as their own, and a Service selecting only `stable`
    would route the canary nothing. Immutable, so it cannot be added to an
    existing Deployment; ADR-022 records why it was taken before any install.
    */}}
    matchLabels:
      {{- include "commerce.deploymentSelectorLabels" . | nindent 6 }}
  template:
    metadata:
      labels:
        {{- include "commerce.labels" . | nindent 8 }}
        app.kubernetes.io/track: {{ include "commerce.track" . }}
      annotations:
        {{- /*
        A config-only deploy changes a ConfigMap and nothing else, so without
        this the pods keep serving the values they started with and the deploy
        reports success (§15.1). Hashing into the pod template is what turns a
        values change into a rollout.

        THE WHOLE OF `.Values`, not the rendered ConfigMap, and the narrower
        version is how this was found. Hashing `commerce.configmap` covers the
        ConfigMap this template mounts and misses every other one: the gateway
        renders `gateway-edge` from its own template (§15.3), so a change to
        `cors.origins` or `ingress.trustedNetworks` rewrote a mounted ConfigMap
        while the pod annotation stayed byte-identical — a silent no-op deploy
        on the two keys most likely to be edited without a rebuild.

        The cost is over-triggering, and it is the safe direction: a change to
        `autoscaling.maxReplicas` or `ingress.host` touches nothing in the
        container and rolls the pods anyway. That is one rollout nobody needed.
        The alternative was a deploy that reported success and changed nothing.

        AND the rendered ConfigMap, because values alone miss the other half: a
        chart-only change to `commerce.config` adds or renames a key with no
        values change at all, so Helm updates the ConfigMap and the annotation
        stays byte-identical. Hashing the render covers what the template does;
        hashing the values covers what the operator does.

        AND every extra ConfigMap's body, which closes the residual this
        comment used to name. A library template cannot reach a file only one
        chart has — but it can include a NAMED template, and the name is
        derivable: each `extraConfigMaps` entry is a suffix, and the chart
        defines `<chart>.<suffix>` holding that ConfigMap's data. So the mount,
        the object's metadata and the hash all come from one value, and a
        chart-only edit to the gateway's edge config now rolls the gateway
        instead of updating an object nothing rereads.
        */}}
        {{- $extra := "" }}
        {{- range .Values.extraConfigMaps }}
        {{- $extra = printf "%s%s" $extra (include (printf "%s.%s" $.Chart.Name .) $) }}
        {{- end }}
        checksum/values: {{ printf "%s%s%s" (toYaml .Values) (include "commerce.configmap" .) $extra | sha256sum }}
    spec:
      {{- /*
      terminationGracePeriodSeconds must exceed the host's own shutdown
      timeout, and 30 is not a margin over 30. HostOptions.ShutdownTimeout
      defaults to 30 seconds and nothing in this solution overrides it —
      measured on .NET 10, not read off a doc page — so a pod given the
      Kubernetes default of 30 is SIGKILLed at the same instant the host would
      have finished draining. §15.3 requires the grace period to exceed the
      longest in-flight operation; the framework's own ceiling is the longest
      one there is, because ServiceOptions.OperationTimeout (20 s) sits inside
      it.
      */}}
      {{- /*
      Nothing in this platform calls the Kubernetes API. Omitting this field
      mounts the namespace's default service-account token into every
      container anyway, so an application compromise — an SSRF, a deserialisation
      bug, anything that can read a file — also hands over a cluster
      credential. Off costs nothing here and removes that from the blast
      radius entirely.
      */}}
      automountServiceAccountToken: false
      terminationGracePeriodSeconds: {{ .Values.terminationGracePeriodSeconds }}
      securityContext:
        {{- /*
        An assertion about the image rather than a change to it: the runtime
        base runs as UID 1654 already (`USER $APP_UID` over a chiselled image,
        §15.2). Stating it here means a base image that starts running as root
        fails to schedule instead of quietly gaining privileges.

        readOnlyRootFilesystem is deliberately NOT set. It is the right posture
        and no chapter has taken the decision, and asserting it untested
        against these images would trade a review question for a CrashLoop.
        */}}
        runAsNonRoot: true
      containers:
        - name: {{ include "commerce.name" . }}
          {{- /*
          Both halves required, like the tag. They were plain interpolations:
          clearing either rendered "/api:sha" or "registry/:sha", which is a
          valid string, an invalid image reference, and a Deployment that never
          pulls. The guard that already covered the tag covers its neighbours.
          */}}
          image: "{{ include "commerce.require" (list .Values.image.registry "image.registry is required: cleared, the image reference has no host and the Deployment never pulls (§15.3).") }}/{{ include "commerce.require" (list .Values.image.api "image.api is required: cleared, the image reference names no repository (§15.3).") }}:{{ include "commerce.tag" . }}"
          imagePullPolicy: {{ .Values.image.pullPolicy }}
          securityContext:
            allowPrivilegeEscalation: false
            capabilities:
              drop: [ALL]
          ports:
            {{- range .Values.ports }}
            - name: {{ .name }}
              containerPort: {{ .containerPort }}
              protocol: TCP
            {{- end }}
          envFrom:
            - configMapRef:
                name: {{ include "commerce.instanceName" . }}-config
            {{- /*
            SUFFIXES, not names. This list held full ConfigMap names, and
            `edge-config.yaml` builds its own from `commerce.name` — so the two
            agreed only while `workload.name` was `gateway`. An overlay setting
            it to `edge` rendered `edge-edge` in one place and mounted
            `gateway-edge` in the other, and the pod sat in
            CreateContainerConfigError.

            One value, two derivations, is the fix: both ends now start from
            `commerce.instanceName`, so there is nothing left to disagree —
            and on the canary track those two names differ from
            `commerce.name`, which is exactly when a stale second derivation
            would have bitten. (This comment said `commerce.name` until PR-25
            moved both ends; the argument never changed, only the helper.)
            */}}
            {{- range .Values.extraConfigMaps }}
            - configMapRef:
                name: {{ printf "%s-%s" (include "commerce.instanceName" $) . }}
            {{- end }}
          {{- /*
          `with`, not a bare include: the gateway owns no database, no broker
          and no client credentials (§10.1, §15.4), so its secret half is
          empty — and a bare `env:` with nothing under it renders `env: null`,
          which the API server accepts and a reader has to decide about.
          */}}
          {{- with include "commerce.env" . | trim }}
          env:
            {{- . | nindent 12 }}
          {{- end }}
          {{- /*
          Three probes, because Kubernetes asks three distinct questions
          (§13.5). Liveness deliberately reaches an endpoint whose predicate
          matches nothing — a liveness probe that checks the database turns a
          brief outage into a restart storm that outlasts it.

          Every path is anonymous by construction: the kubelet carries no
          token, and MapCommonHealthEndpoints calls AllowAnonymous for exactly
          that reason.
          */}}
          livenessProbe:
            httpGet:
              path: {{ .Values.probes.liveness.path }}
              port: {{ .Values.probes.probePort }}
            initialDelaySeconds: {{ .Values.probes.liveness.initialDelaySeconds }}
            periodSeconds: {{ .Values.probes.liveness.periodSeconds }}
          readinessProbe:
            httpGet:
              path: {{ .Values.probes.readiness.path }}
              port: {{ .Values.probes.probePort }}
            initialDelaySeconds: {{ .Values.probes.readiness.initialDelaySeconds }}
            periodSeconds: {{ .Values.probes.readiness.periodSeconds }}
          startupProbe:
            httpGet:
              path: {{ .Values.probes.startup.path }}
              port: {{ .Values.probes.probePort }}
            failureThreshold: {{ .Values.probes.startup.failureThreshold }}
            periodSeconds: {{ .Values.probes.startup.periodSeconds }}
          {{- /*
          A memory limit and no CPU limit, deliberately (§15.3). Memory is
          incompressible, so a leak must be bounded or it takes the node with
          it; CPU is compressible, and a limit throttles into unexplained p99
          spikes well before the pod is short of capacity. Requests still
          reserve what the scheduler must find.
          */}}
          resources:
            {{- toYaml .Values.resources | nindent 12 }}
{{- end -}}
