{{- define "commerce.deployment" -}}
apiVersion: apps/v1
kind: Deployment
metadata:
  {{- /* The instance name, not the workload name: the two differ on the canary
  release, because Helm refuses to touch an object another release owns
  (§15.3). */}}
  name: {{ include "commerce.instanceName" . }}
  labels:
    {{- include "commerce.labels" . | nindent 4 }}
    app.kubernetes.io/track: {{ include "commerce.track" . }}
spec:
  {{- if not .Values.autoscaling.enabled }}
  {{- /* Omitted rather than set to minReplicas while the HPA is on: a managed
  field the chart writes is one the autoscaler writes back after every upgrade
  (§15.3). */}}
  replicas: {{ .Values.replicaCount }}
  {{- end }}
  selector:
    {{- /* The shared selector plus the track, so two Deployments never count
    each other's pods as their own. Immutable, which is why ADR-022 took it
    before any install. */}}
    matchLabels:
      {{- include "commerce.deploymentSelectorLabels" . | nindent 6 }}
  template:
    metadata:
      labels:
        {{- include "commerce.labels" . | nindent 8 }}
        app.kubernetes.io/track: {{ include "commerce.track" . }}
      annotations:
        {{- /* The values, the rendered ConfigMap and each extra ConfigMap's body,
        so that a config-only deploy rolls the pods (§15.3). An extra body is the
        chart's named template `<chart>.<suffix>`, which is the one form of a
        chart's file a library template can reach. */}}
        {{- $extra := "" }}
        {{- range .Values.extraConfigMaps }}
        {{- $extra = printf "%s%s" $extra (include (printf "%s.%s" $.Chart.Name .) $) }}
        {{- end }}
        checksum/values: {{ printf "%s%s%s" (toYaml .Values) (include "commerce.configmap" .) $extra | sha256sum }}
    spec:
      {{- /* Nothing in this platform calls the Kubernetes API, and a mounted
      service-account token would hand a cluster credential to anything able
      to read a file in the container. */}}
      automountServiceAccountToken: false
      {{- /* Above HostOptions.ShutdownTimeout, the longest in-flight operation
      there is, which Kubernetes' default grace period only equals (§15.3). */}}
      terminationGracePeriodSeconds: {{ .Values.terminationGracePeriodSeconds }}
      securityContext:
        {{- /* An assertion about the image, which already runs as a non-root user
        (§15.2), so a base image that starts running as root fails to start.
        readOnlyRootFilesystem is a decision no chapter has taken, and is not
        asserted untested against these images. */}}
        runAsNonRoot: true
      {{- /* Across nodes and zones (§15.3), counted per track and per revision so
      that neither the canary nor a rollout's surge is held back by the other
      pods' placement. */}}
      {{- $skew := int ((.Values.topologySpread).maxSkew | default 0) }}
      {{- if lt $skew 1 }}
      {{- fail "topologySpread.maxSkew is required and must be at least 1: it is how unevenly a workload's replicas may sit across nodes and zones (§15.3)." }}
      {{- end }}
      topologySpreadConstraints:
        {{- range $key, $topologyKey := dict "node" "kubernetes.io/hostname" "zone" "topology.kubernetes.io/zone" }}
        {{- $when := get ((($.Values.topologySpread).whenUnsatisfiable) | default dict) $key | default "" | toString }}
        {{- if not (has $when (list "DoNotSchedule" "ScheduleAnyway")) }}
        {{- fail (printf "topologySpread.whenUnsatisfiable.%s is %q, and must be DoNotSchedule or ScheduleAnyway: the API server accepts nothing else, and refuses it after the upgrade has started (§15.3)." $key $when) }}
        {{- end }}
        - topologyKey: {{ $topologyKey }}
          maxSkew: {{ $skew }}
          whenUnsatisfiable: {{ $when }}
          nodeTaintsPolicy: Honor
          labelSelector:
            matchLabels:
              {{- include "commerce.deploymentSelectorLabels" $ | nindent 14 }}
          matchLabelKeys: [pod-template-hash]
        {{- end }}
      containers:
        - name: {{ include "commerce.name" . }}
          {{- /* Both halves required, like the tag: either one cleared renders a
          valid string, an invalid image reference and a Deployment that never
          pulls. */}}
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
            {{- /* Suffixes, not names: the mount and the chart's own ConfigMap
            both derive from commerce.instanceName, so neither a renamed workload
            nor the canary track can mount one name while rendering another. */}}
            {{- range .Values.extraConfigMaps }}
            - configMapRef:
                name: {{ printf "%s-%s" (include "commerce.instanceName" $) . }}
            {{- end }}
          {{- /* `with`, not a bare include: a host with no secret half, the
          gateway among them (§15.4), would otherwise render `env: null`. */}}
          {{- with include "commerce.env" . | trim }}
          env:
            {{- . | nindent 12 }}
          {{- end }}
          {{- /* Three probes for Kubernetes' three questions (§13.5). Liveness
          checks no dependency, so a brief outage is not a restart storm, and
          every path is anonymous because the kubelet carries no token. */}}
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
          {{- /* A memory limit and no CPU limit, deliberately (§15.3). */}}
          resources:
            {{- toYaml .Values.resources | nindent 12 }}
{{- end -}}
