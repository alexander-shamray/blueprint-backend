# Never built. Dependabot's docker ecosystem reads FROM lines and does not follow Compose's `include`, so this
# mirrors the third-party images the Compose files pin, and a bump here is a red pull request until the files
# that hold the image move with it.
FROM mcr.microsoft.com/mssql/server:2022-CU26-ubuntu-22.04 AS sql
FROM redis:7.4.11-alpine AS redis
FROM quay.io/keycloak/keycloak:26.8.1-1 AS keycloak
FROM chrislusf/seaweedfs:4.47 AS seaweedfs
FROM nginx:1.31.0-alpine AS nginx
FROM otel/opentelemetry-collector-contrib:0.164.0 AS collector
FROM grafana/otel-lgtm:0.38.1 AS lgtm
FROM grafana/k6:2.3.0 AS k6
FROM sheyenrath/wiremock.net:2.19.0 AS wiremock
FROM axllent/mailpit:v1.32.0 AS mailpit
