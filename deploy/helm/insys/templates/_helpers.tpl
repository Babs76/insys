{{- define "insys.secret" -}}
{{- default (printf "%s-secrets" .Release.Name) .Values.existingSecret -}}
{{- end -}}
{{- define "insys.labels" -}}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
app.kubernetes.io/part-of: insys
{{- end -}}
{{- define "insys.secretEnv" -}}
- name: {{ .name }}
  valueFrom:
    secretKeyRef:
      name: {{ include "insys.secret" .root }}
      key: {{ .key }}
{{- end -}}
{{- /*
Keycloak's login theme insys (BR-168), from files/keycloak-theme/insys/, where tools/sync-keycloak-theme.sh copies it from a console commit:
a JSON object from each file's ConfigMap key (its path under the theme, with "/" as "__", as a key can't hold "/") to its content in base64,
so images survive. "{}" when there are no files. The ConfigMap, the Deployment's items and its checksum all come from this one object, and
deploy/keycloak/theme-mount.sh reads the same object back from the ConfigMap (jq -jcS .binaryData) for the Preview's checksum.
*/ -}}
{{- define "insys.keycloakTheme" -}}
{{- $theme := dict -}}
{{- range $path, $_ := .Files.Glob "files/keycloak-theme/insys/**" -}}
{{- $key := trimPrefix "files/keycloak-theme/insys/" $path | replace "/" "__" -}}
{{- if not (regexMatch "^[-._a-zA-Z0-9]+$" $key) -}}
{{- fail (printf "The Keycloak theme's file %s can't be a ConfigMap key: use letters, digits, '.', '-' and '_' only." $path) -}}
{{- end -}}
{{- $_ := set $theme $key ($.Files.Get $path | b64enc) -}}
{{- end -}}
{{- toJson $theme -}}
{{- end -}}
