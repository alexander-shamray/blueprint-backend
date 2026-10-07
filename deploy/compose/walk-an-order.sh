#!/usr/bin/env bash
# One order through a running stack as `demo`, through the gateway, watched on
# the buyer's read until the goods have left (§14.1); README.md says when to run it.
# Exits 0 once the BFF reports dispatched or delivered, 1 on any other end or
# when the deadline passes. Needs curl and jq.
set -euo pipefail

gateway=${GATEWAY:-http://localhost:5000}
keycloak=${KEYCLOAK:-http://localhost:8080}
product=${PRODUCT:-5eed0000-0000-0000-0000-000000000003}
deadline=${DEADLINE_SECONDS:-120}

uuid() {
  cat /proc/sys/kernel/random/uuid 2>/dev/null ||
    python3 -c 'import uuid; print(uuid.uuid4())' 2>/dev/null ||
    py -3 -c 'import uuid; print(uuid.uuid4())'
}

started=$SECONDS
say() { echo "[+$((SECONDS - started))s] $*"; }

token=$(curl -sf "$keycloak/realms/commerce/protocol/openid-connect/token" \
  -d grant_type=password -d client_id=web-app -d username=demo -d password=demo | jq -r .access_token)

body=$(jq -nc --arg id "$(uuid)" --arg product "$product" '{
  commandId: $id,
  items: [{productId: $product, quantity: 1}],
  shippingAddress: {line1: "1 Test Street", city: "Almaty", postalCode: "050000", country: "KZ"},
  currency: "EUR"}')
order=$(curl -sf -X POST "$gateway/api/v1/orders" -H "Authorization: Bearer $token" \
  -H 'Content-Type: application/json' -d "$body" | jq -r .)
say "placed $order"

last=""
while [ $((SECONDS - started)) -lt "$deadline" ]; do
  # A string: before the projection has the order, the answer is a problem whose status is the number 404.
  status=$(curl -s "$gateway/bff/v1/orders/$order" -H "Authorization: Bearer $token" |
    jq -r 'if (.status | type) == "string" then .status else empty end')
  if [ "$status" != "$last" ]; then
    say "the buyer's read says ${status:-nothing yet}"
    last=$status
  fi
  case "$status" in
    dispatched | delivered) exit 0 ;;
    cancelled | out_of_stock | declined) exit 1 ;;
  esac
  sleep 1
done
say "no despatch within ${deadline}s"
exit 1
