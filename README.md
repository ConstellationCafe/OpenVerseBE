# OpenVerseBE

[OpenVerse](https://github.com/ConstellationCafe/OpenVerse)의 룸매치(친선전)를 VPN 없이 중앙 서버에서 매칭하기 위한 백엔드입니다.
방 정보·플레이어·대전 덱을 Redis에 두고, 각 플레이어 PC의 OpenVerse.Api가 이 서버에 방 생성/입장을 요청합니다.

## 구조

```
플레이어 PC (각자)                              집 서버 (Ubuntu 22.04)
┌──────────────────────────┐   HTTPS    ┌───────────────────────────────────────┐
│ Shadowverse 클라이언트    │            │ Caddy :443 ──> OpenVerseBE :5080      │
│   │ hosts → 127.0.0.1    │ ─────────> │                      │                │
│   ▼                      │  /v1/...   │                      ▼                │
│ OpenVerse.Api (로컬)      │            │                    Redis :6379        │
│  카드마스터·에셋·덱 편집   │            │                      ▲  (localhost만)  │
└──────────────────────────┘            │ OpenVerse.Battle :3001 ──┘             │
        │   ws://서버:3001 (대전)        └───────────────────────────────────────┘
        └──────────────────────────────────────────▲
```

- 카드 마스터·에셋·덱 편집은 지금처럼 각자 PC의 로컬 Api가 처리합니다. 게임 데이터를 서버에서 재배포하지 않습니다.
- `open_room/*`, `open_room_battle/*` 요청만 로컬 Api가 이 서버로 넘깁니다.
- 배틀 서버(OpenVerse.Battle)는 같은 Redis에서 대전 덱을 읽습니다.

## API

모든 요청·응답은 snake_case JSON입니다. `/v1/rooms/*`는 `Authorization: Bearer <token>`이 필요합니다.

| Method / Path | 대응하는 클라이언트 요청 | 응답 |
| --- | --- | --- |
| `POST /v1/players/register` | (로컬 Api가 처음 방 기능을 쓸 때) | `viewer_id`, `token` |
| `POST /v1/rooms` | `open_room/create_room` | 방 정보 |
| `GET /v1/rooms/mine` | `open_room_battle/do_matching` | 내가 있는 방, `is_owner` |
| `DELETE /v1/rooms/mine` | `close_room`, `force_release_room` | 204 (방장만 삭제) |
| `PUT /v1/rooms/mine/deck` | `do_matching` 시점의 덱 확정 | 204 |
| `GET /v1/rooms/{id}` | `initialize_room_battle` | 방 정보 |
| `POST /v1/rooms/{id}/enter` | `open_room/enter_room` | 방 정보 / 404 없음 / 409 꽉 참 |
| `POST /v1/rooms/{id}/leave` | `open_room/leave_room` | 204 |
| `GET /healthz` | | `ok` |

`register`는 `OVBE_CAFE_KEY`가 설정되어 있으면 `X-OV-Cafe-Key` 헤더가 같아야 합니다.

### 방 정보 예시

```json
{
  "room_id": "48213", "battle_id": "…", "node_server_url": "ov.example.com:3001",
  "battle_type": 1, "battle_rule": 1, "deck_format": 1, "two_pick_type": 0,
  "can_friend_watch": false, "can_guild_watch": false,
  "is_owner": true,
  "owner": { "viewer_id": 100001, "name": "방장" },
  "visitor": { "viewer_id": 100002, "name": "게스트" }
}
```

## Redis 키

| 키 | 타입 | 내용 | TTL |
| --- | --- | --- | --- |
| `ov:seq:viewer` | String | viewer_id 발급 카운터 (100001부터) | 없음 |
| `ov:player:{udid}` | Hash | `viewer_id`, `name`, `token_hash`, `last_seen` | 없음 |
| `ov:token:{sha256}` | String | 토큰 → udid | 30일 |
| `ov:room:{room_id}` | Hash | 방 설정, 방장/게스트 udid·viewer_id·이름, `node_url` | 3시간 |
| `ov:user_room:{udid}` | String | 플레이어가 있는 room_id | 3시간 |
| `ov:bdeck:{room_id}:{owner\|visitor}` | String (JSON) | 대전 덱. 배틀 서버가 읽음 | 3시간 |

`room`과 `bdeck` 키 이름은 OpenVerse.Battle과의 약속입니다. 바꾸면 양쪽을 같이 바꿔야 합니다.

### 좌석(방장/게스트) 판정

설치 폴더를 복사한 PC들은 같은 viewer_id를 쓰기 때문에, 원래 OpenVerse는 **접속 IP**로 좌석을 구분합니다 (OpenVerse `docs/desync.md`).
이 서버는 덱을 받을 때 요청을 보낸 공인 IP를 `source_ip`로 기록하고, 배틀 서버는 소켓 IP와 비교합니다.
두 사람이 같은 공유기 뒤에 있어 IP가 같으면, 기존과 같이 `RoomCreate`/`RoomEntry` 메시지로 판정합니다.

> 서버 도메인에 IPv6(AAAA) 레코드를 넣지 마세요. 로컬 Api는 IPv6로, 게임은 IPv4로 접속하면 IP가 달라 보여 판정이 메시지 기준으로만 이뤄집니다.

## 설치 (Ubuntu 22.04)

### 1. 패키지

```bash
sudo apt update
sudo apt install -y redis-server debian-keyring debian-archive-keyring apt-transport-https curl git

# Caddy
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' | sudo gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' | sudo tee /etc/apt/sources.list.d/caddy-stable.list
sudo apt update && sudo apt install -y caddy

# .NET 10 SDK (빌드용)
curl -sSL https://dot.net/v1/dotnet-install.sh | sudo bash -s -- --channel 10.0 --install-dir /usr/share/dotnet
sudo ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet
```

### 2. Redis

`/etc/redis/redis.conf`에서 [`deploy/redis-openverse.conf`](deploy/redis-openverse.conf)의 항목을 맞춘 뒤:

```bash
sudo systemctl restart redis-server
redis-cli -a '비밀번호' ping   # PONG
```

### 3. 빌드와 배치

```bash
sudo useradd --system --no-create-home --shell /usr/sbin/nologin openverse

git clone https://github.com/ConstellationCafe/OpenVerseBE.git ~/OpenVerseBE
git clone https://github.com/ConstellationCafe/OpenVerse.git ~/OpenVerse

dotnet publish ~/OpenVerseBE/src/OpenVerseBE/OpenVerseBE.csproj -c Release -r linux-x64 --self-contained false -o /tmp/ovbe
dotnet publish ~/OpenVerse/src/OpenVerse.Battle/OpenVerse.Battle.csproj -c Release -r linux-x64 --self-contained false -o /tmp/ovbattle

sudo mkdir -p /opt/openversebe /opt/openverse-battle/data /etc/openverse
sudo cp -r /tmp/ovbe/. /opt/openversebe/
sudo cp -r /tmp/ovbattle/. /opt/openverse-battle/
# 배틀 서버도 카드 마스터가 필요합니다. 플레이어 PC의 server/data 에서 복사하세요
sudo cp card_master_full.csv.gz /opt/openverse-battle/data/
sudo chown -R openverse:openverse /opt/openversebe /opt/openverse-battle
```

### 4. 설정과 서비스

```bash
sudo cp ~/OpenVerseBE/deploy/openverse.env.example /etc/openverse/openverse.env
sudo nano /etc/openverse/openverse.env      # 비밀번호, 도메인, 카페 키 수정
sudo chown root:openverse /etc/openverse/openverse.env && sudo chmod 640 /etc/openverse/openverse.env

sudo cp ~/OpenVerseBE/deploy/systemd/*.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now openversebe openverse-battle

sudo cp ~/OpenVerseBE/deploy/Caddyfile /etc/caddy/Caddyfile
sudo nano /etc/caddy/Caddyfile              # 도메인 수정
sudo systemctl reload caddy
```

### 5. 방화벽·공유기

```bash
sudo ufw allow 80/tcp     # Caddy 인증서 발급
sudo ufw allow 443/tcp    # OpenVerseBE
sudo ufw allow 3001/tcp   # 배틀 WebSocket
```

공유기에서 80, 443, 3001을 이 서버로 포트포워딩합니다. Redis(6379)와 5080은 열지 않습니다.

### 6. 확인

```bash
curl https://ov.example.com/healthz          # ok
journalctl -u openverse-battle -n 20         # "Engine: ..." 줄 확인
```

## 플레이어 쪽 설정

`openverse-launcher.exe` 옆에 두 파일을 둡니다.

- `be_url.txt`: `https://ov.example.com`
- `cafe_key.txt`: 서버의 `OVBE_CAFE_KEY`와 같은 값

이 파일이 있으면 런처는 로컬 배틀 서버를 띄우지 않고 방 매칭을 이 서버로 보냅니다.

## 개발

```bash
dotnet build OpenVerseBE.slnx
OVBE_TEST_REDIS=127.0.0.1:6379 dotnet test OpenVerseBE.slnx
```

테스트는 실제 Redis에 임의의 키 접두사(`ovtest:…`)로 쓰고 끝나면 지웁니다.

| 환경 변수 | 기본값 | 설명 |
| --- | --- | --- |
| `OVBE_REDIS` | `127.0.0.1:6379` | StackExchange.Redis 연결 문자열 |
| `OVBE_KEY_PREFIX` | `ov:` | 키 접두사 (배틀 서버의 `OPENVERSE_REDIS_PREFIX`와 같아야 함) |
| `OVBE_NODE_URL` | `127.0.0.1:3001` | 클라이언트에 알려줄 배틀 서버 주소 (스킴 없이) |
| `OVBE_CAFE_KEY` | (없음) | 플레이어 등록용 공유 키 |
| `OVBE_ROOM_TTL_MINUTES` | `180` | 방 유지 시간 |
