#!/usr/bin/env bash
# End-to-end check of a running stack: two players register, one opens a room, the other enters, a deck is stored
# where the battle server reads it (with the player's address, not the proxy's), and the battle port accepts a socket
# for that room but refuses one for a room that was never opened.
#
# Run it on the server from the repository folder, after `docker compose up -d`:
#   ./deploy/smoke-test.sh                      # reads OVBE_DOMAIN / OVBE_CAFE_KEY / REDIS_PASSWORD from .env
#   ./deploy/smoke-test.sh --insecure           # self-signed / local certificate (CI uses this with localhost)
set -euo pipefail
cd "$(dirname "$0")/.."

[ -f .env ] && set -a && . ./.env && set +a
DOMAIN="${OVBE_DOMAIN:?OVBE_DOMAIN not set}"
BASE="https://${DOMAIN}"
CURL=(curl -sS --max-time 10)
[ "${1:-}" = "--insecure" ] && CURL+=(-k)
fail() { echo "::error title=smoke::$*"; echo "FAIL: $*" >&2; exit 1; }
ok() { echo "ok  - $*"; }

echo "== waiting for ${BASE}/healthz"
for i in $(seq 1 60); do
  if [ "$("${CURL[@]}" -o /dev/null -w '%{http_code}' "${BASE}/healthz" 2>/dev/null || true)" = "200" ]; then break; fi
  [ "$i" = 60 ] && fail "OpenVerseBE never answered on ${BASE}/healthz"
  sleep 2
done
ok "healthz"

register() {
  "${CURL[@]}" -X POST "${BASE}/v1/players/register" -H 'Content-Type: application/json' \
    -H "X-OV-Cafe-Key: ${OVBE_CAFE_KEY:-}" -d "{\"udid\":\"smoke-$1-$RANDOM$RANDOM\",\"name\":\"smoke-$1\"}"
}
A=$(register owner); B=$(register guest)
TA=$(jq -r .token <<<"$A"); TB=$(jq -r .token <<<"$B")
[ "$TA" != null ] && [ "$TB" != null ] || fail "register failed: $A / $B"
VA=$(jq -r .viewer_id <<<"$A"); VB=$(jq -r .viewer_id <<<"$B")
[ "$VA" != "$VB" ] || fail "both players got viewer_id $VA"
ok "register (viewer ids $VA, $VB)"

ROOM_JSON=$("${CURL[@]}" -X POST "${BASE}/v1/rooms" -H "Authorization: Bearer $TA" -H 'Content-Type: application/json' \
  -d '{"battle_type":1,"battle_rule":1,"deck_format":1,"two_pick_type":0,"can_friend_watch":false,"can_guild_watch":false}')
ROOM=$(jq -r .room_id <<<"$ROOM_JSON")
[[ "$ROOM" =~ ^[0-9]{5}$ ]] || fail "create room: $ROOM_JSON"
NODE=$(jq -r .node_server_url <<<"$ROOM_JSON")
ok "create room $ROOM (battle server $NODE)"

CODE=$("${CURL[@]}" -o /dev/null -w '%{http_code}' -X POST "${BASE}/v1/rooms/${ROOM}/enter" -H "Authorization: Bearer $TB")
[ "$CODE" = 200 ] || fail "enter room: HTTP $CODE"
ok "guest entered"

CODE=$("${CURL[@]}" -o /dev/null -w '%{http_code}' -X PUT "${BASE}/v1/rooms/mine/deck" -H "Authorization: Bearer $TA" \
  -H 'Content-Type: application/json' \
  -d '{"class_id":1,"sub_class_id":10,"chara_id":1,"sleeve_id":3000011,"leader_skin_id":0,"card_ids":[100111010],"user_name":"smoke-owner"}')
[ "$CODE" = 204 ] || fail "save deck: HTTP $CODE"

DECK=$(docker compose exec -T redis redis-cli -a "${REDIS_PASSWORD}" --no-auth-warning GET "ov:bdeck:${ROOM}:owner")
IP=$(jq -r .source_ip <<<"$DECK" 2>/dev/null || true)
[ -n "$IP" ] && [ "$IP" != null ] || fail "deck not in redis or no source_ip: $DECK"
[ "$IP" != "172.28.0.10" ] || fail "source_ip is Caddy's address: X-Forwarded-For is not trusted (OVBE_TRUSTED_PROXIES)"
ok "deck stored for the battle server, source_ip=$IP"

ws() {
  curl -s -o /dev/null -w '%{http_code}' --max-time 3 \
    -H 'Connection: Upgrade' -H 'Upgrade: websocket' -H 'Sec-WebSocket-Version: 13' \
    -H 'Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==' -H "BattleId: $1" -H 'viewerId: 1' \
    "http://${BATTLE_HOST:-${DOMAIN}}:3001/?EIO=4&transport=websocket" || true
}
CODE=$(ws "$ROOM"); [ "$CODE" = 101 ] || fail "battle socket for room $ROOM: HTTP $CODE (expected 101)"
ok "battle socket accepted for the open room"
CODE=$(ws 00000); [ "$CODE" = 403 ] || fail "battle socket for unknown room: HTTP $CODE (expected 403)"
ok "battle socket refused for an unknown room"

"${CURL[@]}" -o /dev/null -X DELETE "${BASE}/v1/rooms/mine" -H "Authorization: Bearer $TA"
echo "::notice title=smoke::all checks passed (room $ROOM, source_ip $IP)"
echo "ALL OK"
