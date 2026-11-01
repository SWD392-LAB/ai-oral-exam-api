#!/usr/bin/env bash
set -euo pipefail

keycloak_admin_token="$(
  curl --fail --silent --show-error \
    --data-urlencode "grant_type=password" \
    --data-urlencode "client_id=admin-cli" \
    --data-urlencode "username=ci-admin" \
    --data-urlencode "password=$KEYCLOAK_CI_BOOTSTRAP_PASSWORD" \
    http://localhost:8080/realms/master/protocol/openid-connect/token \
    | jq -r '.access_token'
)"
test_client_secret="$(openssl rand -hex 32)"
test_client_id="aives-ci-api-smoke"
client_json="$(
  jq -n --arg secret "$test_client_secret" '{
    clientId: "aives-ci-api-smoke",
    enabled: true,
    protocol: "openid-connect",
    publicClient: false,
    clientAuthenticatorType: "client-secret",
    secret: $secret,
    standardFlowEnabled: false,
    directAccessGrantsEnabled: false,
    implicitFlowEnabled: false,
    serviceAccountsEnabled: true,
    protocolMappers: [{
      name: "CI API audience",
      protocol: "openid-connect",
      protocolMapper: "oidc-audience-mapper",
      consentRequired: false,
      config: {
        "included.client.audience": "aives-api",
        "access.token.claim": "true",
        "id.token.claim": "false"
      }
    }]
  }'
)"

curl --fail --silent --show-error \
  --request POST \
  --header "Authorization: Bearer $keycloak_admin_token" \
  --header "Content-Type: application/json" \
  --data "$client_json" \
  http://localhost:8080/admin/realms/aives/clients

client_uuid="$(
  curl --fail --silent --show-error \
    --header "Authorization: Bearer $keycloak_admin_token" \
    "http://localhost:8080/admin/realms/aives/clients?clientId=$test_client_id" \
    | jq -er '.[0].id'
)"
service_account_id="$(
  curl --fail --silent --show-error \
    --header "Authorization: Bearer $keycloak_admin_token" \
    "http://localhost:8080/admin/realms/aives/clients/$client_uuid/service-account-user" \
    | jq -er '.id'
)"
service_account_username="ci_kc_${service_account_id//-/}"
service_account_email="$service_account_username@ci.example.test"

PGPASSWORD=postgres PGOPTIONS="-c search_path=fresh" psql \
  -h localhost -U postgres -d aives_f1_concurrency_test \
  -v ON_ERROR_STOP=1 \
  -c "INSERT INTO fresh.users (id, role_id, full_name, email, username, is_active, created_at) SELECT '$service_account_id'::uuid, id, 'CI Keycloak smoke user', '$service_account_email', '$service_account_username', TRUE, now() FROM fresh.roles WHERE name = 'Administrator'"

access_token="$(
  curl --fail --silent --show-error \
    --data-urlencode "grant_type=client_credentials" \
    --data-urlencode "client_id=$test_client_id" \
    --data-urlencode "client_secret=$test_client_secret" \
    http://localhost:8080/realms/aives/protocol/openid-connect/token \
    | jq -er '.access_token'
)"

api_log="$(mktemp)"
ASPNETCORE_ENVIRONMENT=Development \
ASPNETCORE_URLS=http://localhost:5080 \
ConnectionStrings__Database='Host=localhost;Port=5432;Database=aives_f1_concurrency_test;Username=postgres;Password=postgres;Search Path=fresh' \
Keycloak__Authority=http://localhost:8080/realms/aives \
Keycloak__Audience=aives-api \
KeycloakAdmin__ClientSecret="$test_client_secret" \
dotnet src/AiOralExam.Api/bin/Release/net8.0/AiOralExam.Api.dll >"$api_log" 2>&1 &
api_pid=$!
trap 'kill "$api_pid" 2>/dev/null || true; rm -f "$api_log"' EXIT

for attempt in {1..60}; do
  status="$(
    curl --silent --output /dev/null --write-out '%{http_code}' \
      http://localhost:5080/api/auth/me || true
  )"
  if [[ "$status" == "401" ]]; then
    break
  fi
  sleep 2
done

if [[ "${status:-}" != "401" ]]; then
  cat "$api_log"
  echo "The API did not challenge an unauthenticated profile request (status: ${status:-unknown})." >&2
  exit 1
fi

for attempt in {1..30}; do
  status="$(
    curl --silent --output /dev/null --write-out '%{http_code}' \
      --header "Authorization: Bearer $access_token" \
      http://localhost:5080/api/auth/me || true
  )"
  if [[ "$status" == "200" ]]; then
    exit 0
  fi
  sleep 2
done

cat "$api_log"
echo "The Keycloak-issued access token was rejected by the API (status: ${status:-unknown})." >&2
exit 1
