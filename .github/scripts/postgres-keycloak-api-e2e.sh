#!/usr/bin/env bash
# Runs the role-authenticated lifecycle against the isolated PostgreSQL/Keycloak CI services.
set -euo pipefail

api_url="http://localhost:5080"
realm_url="http://localhost:8080/realms/aives"
admin_url="http://localhost:8080/admin/realms/aives"
student_id="11111111-0000-0000-0000-000000000006"
lecturer_id="11111111-0000-0000-0000-000000000002"
course_id="22222222-0000-0000-0000-000000000001"
api_log="$(mktemp)"
audio_file="$(mktemp --suffix=.wav)"
csv_file="$(mktemp)"
api_pid=""

cleanup() {
  local exit_code=$?
  if [[ "$exit_code" -ne 0 ]]; then
    echo "API diagnostic tail (last 60 lines):" >&2
    tail -n 60 "$api_log" >&2
  fi
  if [[ -n "$api_pid" ]] && kill -0 "$api_pid" 2>/dev/null; then
    kill "$api_pid"
    wait "$api_pid" || true
  fi
  rm -f "$api_log" "$audio_file" "$csv_file"
  return "$exit_code"
}
trap 'echo "E2E failed at line ${LINENO}; API output below is limited to 60 lines." >&2' ERR
trap cleanup EXIT

keycloak_admin_token="$(
  curl --fail --silent --show-error \
    --data-urlencode "grant_type=password" \
    --data-urlencode "client_id=admin-cli" \
    --data-urlencode "username=ci-admin" \
    --data-urlencode "password=${KEYCLOAK_CI_BOOTSTRAP_PASSWORD:?Missing disposable Keycloak bootstrap password}" \
    http://localhost:8080/realms/master/protocol/openid-connect/token \
    | jq -er '.access_token'
)"

test_client_id="aives-ci-e2e"
client_json="$(
  jq -n '{
    clientId: "aives-ci-e2e",
    enabled: true,
    protocol: "openid-connect",
    publicClient: true,
    standardFlowEnabled: false,
    directAccessGrantsEnabled: true,
    implicitFlowEnabled: false,
    serviceAccountsEnabled: false,
    fullScopeAllowed: true,
    protocolMappers: [
      {
        name: "CI API audience",
        protocol: "openid-connect",
        protocolMapper: "oidc-audience-mapper",
        consentRequired: false,
        config: {
          "included.client.audience": "aives-api",
          "access.token.claim": "true",
          "id.token.claim": "false"
        }
      },
      {
        name: "application realm roles",
        protocol: "openid-connect",
        protocolMapper: "oidc-usermodel-realm-role-mapper",
        consentRequired: false,
        config: {
          "claim.name": "role",
          "jsonType.label": "String",
          "multivalued": "true",
          "access.token.claim": "true",
          "id.token.claim": "false"
        }
      }
    ]
  }'
)"

curl --fail --silent --show-error \
  --request POST \
  --header "Authorization: Bearer $keycloak_admin_token" \
  --header "Content-Type: application/json" \
  --data "$client_json" \
  "$admin_url/clients" >/dev/null

admin_client_uuid="$(
  curl --fail --silent --show-error \
    --header "Authorization: Bearer $keycloak_admin_token" \
    "$admin_url/clients?clientId=aives-admin-api" \
    | jq -er '.[0].id'
)"
admin_client_secret="$(
  curl --fail --silent --show-error \
    --header "Authorization: Bearer $keycloak_admin_token" \
    "$admin_url/clients/$admin_client_uuid/client-secret" \
    | jq -er '.value'
)"

get_user_token() {
  local username="$1"
  curl --fail --silent --show-error \
    --data-urlencode "grant_type=password" \
    --data-urlencode "client_id=$test_client_id" \
    --data-urlencode "username=$username" \
    --data-urlencode "password=Password@123" \
    "$realm_url/protocol/openid-connect/token" \
    | jq -er '.access_token'
}

student_token="$(get_user_token han.hg)"
lecturer_token="$(get_user_token an.nv)"

api_json() {
  local token="$1"
  local method="$2"
  local url="$3"
  local body="${4-}"
  if [[ -n "$body" ]]; then
    curl --fail-with-body --silent --show-error \
      --request "$method" \
      --header "Authorization: Bearer $token" \
      --header "Content-Type: application/json" \
      --data "$body" "$url"
  else
    curl --fail-with-body --silent --show-error \
      --request "$method" \
      --header "Authorization: Bearer $token" "$url"
  fi
}

start_api() {
  env \
  ASPNETCORE_ENVIRONMENT=Development \
  ASPNETCORE_URLS="$api_url" \
  ConnectionStrings__Database='Host=localhost;Port=5432;Database=aives_f1_concurrency_test;Username=postgres;Password=postgres;Search Path=e2e' \
  Keycloak__Authority="$realm_url" \
  Keycloak__Audience=aives-api \
  KeycloakAdmin__ClientSecret="$admin_client_secret" \
  AiProviders__LlmProvider=Mock \
  AiProviders__SttProvider=Mock \
  AiProviders__TtsProvider=Mock \
  Interview__TimeLimitGraceSeconds=0 \
  Logging__LogLevel__Default=Warning \
  "Logging__LogLevel__Microsoft.AspNetCore=Warning" \
  "Logging__LogLevel__Microsoft.EntityFrameworkCore=Warning" \
  dotnet src/AiOralExam.Api/bin/Release/net8.0/AiOralExam.Api.dll >>"$api_log" 2>&1 &
  api_pid=$!

  for attempt in {1..60}; do
    status="$(
      curl --silent --output /dev/null --write-out '%{http_code}' \
        "$api_url/api/auth/me" || true
    )"
    if [[ "$status" == "401" ]]; then
      return
    fi
    sleep 2
  done

  echo "The API did not challenge an unauthenticated profile request (status: ${status:-unknown})." >&2
  exit 1
}

stop_api() {
  kill "$api_pid"
  wait "$api_pid" || true
  api_pid=""
}

create_exam() {
  local title="$1"
  local time_limit="$2"
  local start_time
  local end_time
  local response
  local session_id
  local item_response
  local bank_item_id
  local bank_item_content
  local attached_item_ids
  local approved_item
  local publish

  start_time="$(date -u --date='1 minute ago' '+%Y-%m-%dT%H:%M:%SZ')"
  end_time="$(date -u --date='1 hour' '+%Y-%m-%dT%H:%M:%SZ')"
  response="$(
    api_json "$lecturer_token" POST "$api_url/api/exam-sessions" \
      "$(jq -n --arg course "$course_id" --arg title "$title" \
        --arg start "$start_time" --arg end_at "$end_time" --argjson limit "$time_limit" \
        '{courseId:$course,title:$title,startTime:$start,endTime:$end_at,timeLimitPerAnswerSeconds:$limit,maxFollowUps:0}')"
  )" || return
  session_id="$(jq -er '.id' <<<"$response")" || return

  item_response="$(
    api_json "$lecturer_token" POST "$api_url/api/courses/$course_id/question-bank" \
      "$(jq -n \
        --arg topic 'CI Question Bank' \
        --arg bloom 'Understand' \
        --arg content "Explain the purpose of the $title oral examination." \
        '{topic:$topic,bloom:$bloom,content:$content}')"
  )" || return
  bank_item_id="$(jq -er '.id' <<<"$item_response")" || return
  bank_item_content="$(jq -er '.content' <<<"$item_response")" || return
  approved_item="$(api_json "$lecturer_token" POST "$api_url/api/courses/$course_id/question-bank/$bank_item_id/approve")" || return
  [[ "$(jq -er '.status' <<<"$approved_item")" == "Approved" ]] || return

  attached_item_ids="$(
    api_json "$lecturer_token" POST "$api_url/api/exam-sessions/$session_id/questions/from-bank" \
      "$(jq -n --arg item_id "$bank_item_id" '{itemIds:[$item_id]}')"
  )" || return
  jq -e --arg item_id "$bank_item_id" '. == [$item_id]' <<<"$attached_item_ids" >/dev/null || return
  jq -e --arg expected "$bank_item_content" '. | length == 1 and .[0].content == $expected' \
    <<<"$(api_json "$lecturer_token" GET "$api_url/api/exam-sessions/$session_id/questions")" >/dev/null || return

  api_json "$lecturer_token" POST "$api_url/api/exam-sessions/$session_id/participants/import" \
    '{"studentCodes":["SE180003"]}' >/dev/null || return
  publish="$(api_json "$lecturer_token" POST "$api_url/api/exam-sessions/$session_id/publish")" || return
  [[ "$(jq -er '.status' <<<"$publish")" == "Published" ]] || return

  printf '%s\n' "$session_id"
}

start_attempt() {
  local session_id="$1"
  local response
  response="$(
    api_json "$student_token" POST "$api_url/api/interview-attempts" \
      "$(jq -n --arg session "$session_id" '{examSessionId:$session}')"
  )" || return
  printf '%s\n' "$response"
}

review_attempt() {
  local attempt_id="$1"
  local draft
  local confirmation

  draft="$(
    api_json "$lecturer_token" PUT "$api_url/api/interview-attempts/$attempt_id/score-review/draft" \
      '{"finalScore":8.5,"comment":"CI end-to-end lecturer feedback."}'
  )"
  [[ "$(jq -er '.status' <<<"$draft")" == "Pending" ]]
  confirmation="$(
    api_json "$lecturer_token" POST "$api_url/api/interview-attempts/$attempt_id/score-review/confirm"
  )"
  [[ "$(jq -er '.status' <<<"$confirmation")" == "Approved" ]]
}

start_api

unauthenticated_status="$(
  curl --silent --output /dev/null --write-out '%{http_code}' "$api_url/api/auth/me"
)"
[[ "$unauthenticated_status" == "401" ]]
api_json "$student_token" GET "$api_url/api/auth/me" \
  | jq -e --arg user_id "$student_id" '.id == $user_id and .role == "Student"' >/dev/null
api_json "$lecturer_token" GET "$api_url/api/auth/me" \
  | jq -e --arg user_id "$lecturer_id" '.id == $user_id and .role == "Lecturer"' >/dev/null

forbidden_status="$(
  curl --silent --output /dev/null --write-out '%{http_code}' \
    --request POST \
    --header "Authorization: Bearer $student_token" \
    --header "Content-Type: application/json" \
    --data '{}' "$api_url/api/exam-sessions"
)"
[[ "$forbidden_status" == "403" ]]

text_session_id="$(create_exam 'CI PostgreSQL Keycloak text E2E' 180)"
lecturer_forbidden_status="$(
  curl --silent --output /dev/null --write-out '%{http_code}' \
    --request POST \
    --header "Authorization: Bearer $lecturer_token" \
    --header "Content-Type: application/json" \
    --data "$(jq -n --arg session "$text_session_id" '{examSessionId:$session}')" \
    "$api_url/api/interview-attempts"
)"
[[ "$lecturer_forbidden_status" == "403" ]]

text_attempt="$(start_attempt "$text_session_id")"
text_attempt_id="$(jq -er '.attemptId' <<<"$text_attempt")"
text_turn_id="$(jq -er '.currentTurn.turnId' <<<"$text_attempt")"

stop_api
start_api

resumed_state="$(api_json "$student_token" GET "$api_url/api/interview-attempts/$text_attempt_id")"
[[ "$(jq -er '.attemptId' <<<"$resumed_state")" == "$text_attempt_id" ]]
restarted_attempt="$(api_json "$student_token" POST "$api_url/api/interview-attempts" \
  "$(jq -n --arg session "$text_session_id" '{examSessionId:$session}')")"
[[ "$(jq -er '.attemptId' <<<"$restarted_attempt")" == "$text_attempt_id" ]]

text_answer='{"answerText":"A CI answer after reconnect.","turnId":"'"$text_turn_id"'"}'
text_result="$(
  api_json "$student_token" POST "$api_url/api/interview-attempts/$text_attempt_id/responses" "$text_answer"
)"
[[ "$(jq -er '.attempt.status' <<<"$text_result")" == "PendingReview" ]]
retry_result="$(
  api_json "$student_token" POST "$api_url/api/interview-attempts/$text_attempt_id/responses" "$text_answer"
)"
[[ "$(jq -er '.duplicate' <<<"$retry_result")" == "true" ]]

pending_report="$(api_json "$student_token" GET "$api_url/api/reports/attempts/$text_attempt_id")"
jq -e '.finalScore == null and (.questions | length) == 0' <<<"$pending_report" >/dev/null
review_attempt "$text_attempt_id"
final_report="$(api_json "$student_token" GET "$api_url/api/reports/attempts/$text_attempt_id")"
jq -e '.finalScore == 8.5 and .lecturerComment == "CI end-to-end lecturer feedback."' <<<"$final_report" >/dev/null

statistics="$(api_json "$lecturer_token" GET "$api_url/api/reports/exam-sessions/$text_session_id/statistics")"
jq -e '.completedAttempts >= 1 and .finalizedAttempts >= 1' <<<"$statistics" >/dev/null
curl --fail --silent --show-error \
  --header "Authorization: Bearer $lecturer_token" \
  "$api_url/api/reports/exam-sessions/$text_session_id/grade-sheet" >"$csv_file"
grep -q 'SE180003' "$csv_file"
grep -q '8.5' "$csv_file"

student_report_forbidden="$(
  curl --silent --output /dev/null --write-out '%{http_code}' \
    --header "Authorization: Bearer $student_token" \
    "$api_url/api/reports/exam-sessions/$text_session_id/statistics"
)"
[[ "$student_report_forbidden" == "403" ]]

voice_session_id="$(create_exam 'CI PostgreSQL Keycloak voice E2E' 180)"
voice_attempt="$(start_attempt "$voice_session_id")"
voice_attempt_id="$(jq -er '.attemptId' <<<"$voice_attempt")"
voice_turn_id="$(jq -er '.currentTurn.turnId' <<<"$voice_attempt")"

# The mock STT provider accepts a small stand-in payload; this exercises the HTTP voice contract.
printf 'CI simulated audio payload' >"$audio_file"
first_chunk="$(
  curl --fail-with-body --silent --show-error \
    --request POST \
    --header "Authorization: Bearer $student_token" \
    --form "chunk=@$audio_file;type=audio/wav" \
    "$api_url/api/interview-attempts/$voice_attempt_id/audio/chunks?turnId=$voice_turn_id&sequence=0"
)"
[[ "$(jq -er '.duplicate' <<<"$first_chunk")" == "false" ]]
retried_chunk="$(
  curl --fail-with-body --silent --show-error \
    --request POST \
    --header "Authorization: Bearer $student_token" \
    --form "chunk=@$audio_file;type=audio/wav" \
    "$api_url/api/interview-attempts/$voice_attempt_id/audio/chunks?turnId=$voice_turn_id&sequence=0"
)"
[[ "$(jq -er '.duplicate' <<<"$retried_chunk")" == "true" ]]
checkpoint="$(
  api_json "$student_token" GET \
    "$api_url/api/interview-attempts/$voice_attempt_id/audio/checkpoint?turnId=$voice_turn_id"
)"
jq -e '.nextSequence == 1 and .isCompleted == false' <<<"$checkpoint" >/dev/null
voice_result="$(
  api_json "$student_token" POST \
    "$api_url/api/interview-attempts/$voice_attempt_id/audio/complete?turnId=$voice_turn_id"
)"
[[ "$(jq -er '.attempt.status' <<<"$voice_result")" == "PendingReview" ]]
voice_review="$(api_json "$lecturer_token" GET "$api_url/api/interview-attempts/$voice_attempt_id/review")"
jq -e --arg transcript "[Mock STT] This is a simulated transcript of the answer." \
  '[.responses[].turns[].answerTranscript] | index($transcript) != null' \
  <<<"$voice_review" >/dev/null
review_attempt "$voice_attempt_id"

timeout_session_id="$(create_exam 'CI PostgreSQL Keycloak timeout E2E' 5)"
timeout_attempt="$(start_attempt "$timeout_session_id")"
timeout_attempt_id="$(jq -er '.attemptId' <<<"$timeout_attempt")"
timeout_turn_id="$(jq -er '.currentTurn.turnId' <<<"$timeout_attempt")"

# Expire the turn while the deadline worker is stopped; the restarted worker must recover it.
stop_api
sleep 7
start_api

timed_out=false
for attempt in {1..20}; do
  timeout_state="$(api_json "$student_token" GET "$api_url/api/interview-attempts/$timeout_attempt_id")"
  if [[ "$(jq -er '.status' <<<"$timeout_state")" == "PendingReview" ]]; then
    timed_out=true
    break
  fi
  sleep 1
done
[[ "$timed_out" == "true" ]]
late_answer_status="$(
  curl --silent --output /dev/null --write-out '%{http_code}' \
    --request POST \
    --header "Authorization: Bearer $student_token" \
    --header "Content-Type: application/json" \
    --data "$(jq -n --arg turn "$timeout_turn_id" '{answerText:"Too late",turnId:$turn}')" \
    "$api_url/api/interview-attempts/$timeout_attempt_id/responses"
)"
[[ "$late_answer_status" == "409" ]]
