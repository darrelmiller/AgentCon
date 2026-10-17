{{- define "seller.fullname" -}}
{{- printf "agentcon-%s" .Values.agentName | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "seller.labels" -}}
app.kubernetes.io/name: agentcon-seller
app.kubernetes.io/instance: {{ include "seller.fullname" . }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version }}
{{- end -}}

{{- define "seller.selectorLabels" -}}
app.kubernetes.io/name: agentcon-seller
app.kubernetes.io/instance: {{ include "seller.fullname" . }}
{{- end -}}

{{- define "seller.did" -}}
{{- printf "%s:%s" .Values.identity.didWebBase .Values.agentName -}}
{{- end -}}

{{- define "seller.identitySecret" -}}
{{- .Values.identity.existingSecret | default (printf "%s-identity" (include "seller.fullname" .)) -}}
{{- end -}}

{{- define "seller.modelSecret" -}}
{{- .Values.modelCreds.existingSecret | default (printf "%s-model" (include "seller.fullname" .)) -}}
{{- end -}}

{{/* The supplier's SLIM config: SPIRE mTLS to the node, did:web identity, peers checked against the published JWKS. */}}
{{- define "seller.slimConfig" -}}
{{- $spire := dict "type" "spire" "socket_path" .Values.slim.spireSocket -}}
{{- $audience := list .Values.identity.audience -}}
{{- $config := dict
  "client" (dict
    "endpoint" .Values.slim.endpoint
    "tls" (dict "source" $spire "ca_source" $spire "tls_version" .Values.slim.tlsVersion "include_system_ca_certs_pool" false))
  "app" (dict
    "name" (printf "%s/%s" .Values.slim.organization .Values.agentName)
    "identity_provider" (dict
      "type" "jwt"
      "claims" (dict "issuer" .Values.identity.issuer "subject" (include "seller.did" .) "audience" $audience)
      "duration" "1h"
      "key" (dict "type" "encoding" "algorithm" "EdDSA" "format" "pem" "key" (dict "file" "/var/run/secrets/identity/key.pem")))
    "identity_verifier" (dict
      "type" "jwt"
      "claims" (dict "issuer" .Values.identity.issuer "audience" $audience)
      "duration" "1h"
      "key" (dict "type" "decoding" "algorithm" "EdDSA" "format" "jwks" "key" (dict "file" "/var/run/agentcon/jwks.json"))
      "subject_binding" "enforce")) -}}
{{- toPrettyJson $config -}}
{{- end -}}
