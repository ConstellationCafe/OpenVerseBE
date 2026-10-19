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
| `POST /v1/rooms` | `open_room/create_room` | 방 정보 / 503 `room_limit` (방 개수 한도) |
| `GET /v1/rooms/mine` | `open_room_battle/do_matching` | 내가 있는 방, `is_owner` |
| `DELETE /v1/rooms/mine` | `close_room`, `force_release_room` | 204 (방장만 삭제) |
| `PUT /v1/rooms/mine/deck` | `do_matching` 시점의 덱 확정 | 204 |
| `GET /v1/rooms/{id}` | `initialize_room_battle` | 방 정보 |
| `POST /v1/rooms/{id}/enter` | `open_room/enter_room` | 방 정보 / 404 없음 / 409 꽉 참 |
| `POST /v1/rooms/{id}/leave` | `open_room/leave_room` | 204 |
| `GET /healthz` | | `ok` |
| `GET /v1/status` | | `open_rooms`, `max_rooms` (인증 불필요) |

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
| `ov:rooms` | Set | 열려 있는 room_id 목록. 방 개수 한도 계산용 (만료된 방은 다음 방 생성 때 정리) | 없음 |
| `ov:user_room:{udid}` | String | 플레이어가 있는 room_id | 3시간 |
| `ov:bdeck:{room_id}:{owner\|visitor}` | String (JSON) | 대전 덱. 배틀 서버가 읽음 | 3시간 |

`room`과 `bdeck` 키 이름은 OpenVerse.Battle과의 약속입니다. 바꾸면 양쪽을 같이 바꿔야 합니다.

### 좌석(방장/게스트) 판정

설치 폴더를 복사한 PC들은 같은 viewer_id를 쓰기 때문에, 원래 OpenVerse는 **접속 IP**로 좌석을 구분합니다 (OpenVerse `docs/desync.md`).
이 서버는 덱을 받을 때 요청을 보낸 공인 IP를 `source_ip`로 기록하고, 배틀 서버는 소켓 IP와 비교합니다.
두 사람이 같은 공유기 뒤에 있어 IP가 같으면, 기존과 같이 `RoomCreate`/`RoomEntry` 메시지로 판정합니다.

> 서버 도메인에 IPv6(AAAA) 레코드를 넣지 마세요. 로컬 Api는 IPv6로, 게임은 IPv4로 접속하면 IP가 달라 보여 판정이 메시지 기준으로만 이뤄집니다.

## 설치 (Ubuntu 22.04, Docker Compose)

Redis, OpenVerseBE, 배틀 서버가 컨테이너로 한 번에 뜹니다. 서버에 .NET이나 Redis를 따로 설치할 필요가 없습니다.
HTTPS는 **서버에 이미 있는 nginx**가 맡거나(기본), 80/443이 비어 있으면 함께 들어 있는 Caddy를 씁니다.

```
              인터넷
     :443 (기존 nginx)            :3001
          │                          │
   ┌──────┼──── docker compose (openverse) ─────┼──────────┐
   │      ▼ 127.0.0.1:5080                      ▼          │
   │   be (OpenVerseBE) ──► redis ◄── battle (OpenVerse.Battle)
   │                     (외부 비공개)                       │
   └─────────────────────────────────────────────────────────┘
```

### 1. Docker 설치

```bash
sudo apt update
sudo apt install -y ca-certificates curl git jq
sudo install -m 0755 -d /etc/apt/keyrings
sudo curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo "$VERSION_CODENAME") stable" \
  | sudo tee /etc/apt/sources.list.d/docker.list > /dev/null
sudo apt update
sudo apt install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
sudo usermod -aG docker $USER   # 다시 로그인하면 sudo 없이 docker 사용
```

### 2. 설정

```bash
git clone https://github.com/ConstellationCafe/OpenVerseBE.git ~/OpenVerseBE
cd ~/OpenVerseBE
cp .env.example .env
nano .env            # OVBE_DOMAIN, REDIS_PASSWORD, OVBE_CAFE_KEY 수정

mkdir -p data
# 배틀 서버가 쓰는 카드 마스터. 플레이어 PC의 OpenVerse server/data 폴더에서 복사
cp /어딘가/card_master_full.csv.gz data/
```

`.env` 항목:

| 이름 | 설명 |
| --- | --- |
| `OVBE_DOMAIN` | 서버 도메인(DDNS 가능). **A 레코드만** 두고 AAAA(IPv6)는 넣지 않습니다 |
| `REDIS_PASSWORD` | 영문·숫자만 |
| `OVBE_CAFE_KEY` | 플레이어가 `cafe_key.txt`에 넣을 값. 비우면 누구나 등록 가능 |
| `OVBE_ROOM_TTL_MINUTES` | 방 유지 시간(분), 기본 180 |
| `OVBE_MAX_ROOMS` | 서버 전체에서 동시에 열 수 있는 방 개수, 기본 10. `0`이면 제한 없음 |
| `OPENVERSE_REF` | 배틀 서버를 빌드할 OpenVerse 브랜치 (기본 `main`) |
| `OVBE_DATA_DIR` | 카드 마스터 폴더. 기본은 저장소의 `data/`. 자동 배포(CD)를 쓰면 저장소 밖으로 지정 |
| `OVBE_PUBLIC_URL` | 도메인 하나를 WebUI 등과 같이 쓸 때만. 예: `https://example.iptime.org/openverse` |

### 3. HTTPS: 기존 nginx에 연결 (기본)

OpenVerseBE는 서버 안에서만 열린 `127.0.0.1:5080`에 뜹니다. nginx가 여기로 넘겨주게 합니다. 둘 중 하나를 고릅니다.

**A. 도메인이 하나뿐이고 이미 WebUI 등이 쓰고 있다** (ipTIME DDNS 등): 하위 경로 `/openverse/`로 붙입니다.

1. 기존 사이트 설정(`/etc/nginx/sites-available/...`)의 **HTTPS `server { }` 블록 안에** [`deploy/nginx/openverse-path.conf`](deploy/nginx/openverse-path.conf)의 `location /openverse/ { ... }` 부분을 붙여 넣습니다.
2. `sudo nginx -t && sudo systemctl reload nginx`
3. `.env`에 `OVBE_PUBLIC_URL=https://도메인/openverse`를 추가합니다.
4. 플레이어의 `be_url.txt`: `https://도메인/openverse`

**B. OpenVerse 전용 도메인(서브도메인)이 있다**: [`deploy/nginx/openverse-subdomain.conf`](deploy/nginx/openverse-subdomain.conf) 파일 맨 위 주석대로 사이트를 추가하고 `certbot --nginx`로 HTTPS를 붙입니다. 플레이어의 `be_url.txt`: `https://그도메인`

어느 쪽이든 nginx 설정의 `proxy_set_header X-Forwarded-For $remote_addr;` 줄은 빼면 안 됩니다. 플레이어의 실제 IP가 이 줄로 전달되고, 배틀 서버는 그 IP로 방장과 게스트를 구분합니다.

> 80/443을 쓰는 프로그램이 없는 서버라면 nginx 대신 `docker compose --profile caddy up -d --build`로 띄웁니다. 함께 들어 있는 Caddy가 `OVBE_DOMAIN`으로 인증서를 자동으로 받습니다.

### 4. 포트

공유기에서 **3001**(배틀)을 이 서버로 포트포워딩합니다. 443(그리고 인증서 발급용 80)은 nginx가 이미 쓰고 있다면 포워딩되어 있을 것입니다.

```bash
sudo ufw allow 3001/tcp      # ufw를 쓰는 경우
```

> Docker가 연 포트는 ufw 규칙을 거치지 않고 열립니다. compose가 외부에 여는 포트는 3001뿐이고(Caddy를 쓰면 80/443 추가), OpenVerseBE는 127.0.0.1에만 열립니다.

### 5. 실행과 확인

```bash
docker compose up -d --build     # 첫 빌드는 몇 분 걸립니다
docker compose ps                # redis, be, battle 이 running
./deploy/smoke-test.sh           # 등록 → 방 생성 → 입장 → 덱 → 배틀 소켓까지 확인, 마지막 줄 ALL OK
```

`smoke-test.sh`는 플레이어와 같은 주소(`OVBE_PUBLIC_URL` 또는 `https://OVBE_DOMAIN`)로 접속해서 확인하므로, nginx·인증서·포트포워딩까지 맞아야 통과합니다.
nginx가 플레이어 IP를 넘기지 않으면 "source_ip is the docker gateway"로 실패합니다.

### 운영

```bash
docker compose logs -f be battle         # 로그
git pull && docker compose up -d --build # 업데이트 (OpenVerse 쪽 변경도 다시 빌드됨)
docker compose down                      # 정지 (데이터는 볼륨에 남음)
```

플레이어 정보(viewer_id)는 `redis-data` 볼륨에 저장되어 재시작해도 유지됩니다.

### 주의: 플레이어 IP가 제대로 기록되는지

좌석 판정에 플레이어의 공인 IP가 쓰이므로, 이 IP가 프록시 주소로 찍히면 안 됩니다.
OpenVerseBE는 도커 게이트웨이(172.28.0.1, 호스트의 nginx가 들어오는 길)와 Caddy(172.28.0.10)가 보낸 `X-Forwarded-For`만 믿습니다(`OVBE_TRUSTED_PROXIES`). `smoke-test.sh`가 이것도 검사합니다.
Docker 데몬 설정에서 `"userland-proxy"`나 `"iptables": false`를 바꿨다면 배틀 소켓의 출발지 IP가 바뀔 수 있으니 기본값으로 두세요.

### 자동 배포 (CD)

main에 push(PR 머지)되면 [`.github/workflows/CD.yml`](.github/workflows/CD.yml)이 서버에 배포합니다. WebUI_BE와 같은 방식입니다.
저장소를 `/home/elaina/dockers/OpenVerseBE/service`로 복사하고, SSH로 `docker compose up -d --build`를 실행한 뒤 `/healthz`를 확인합니다.

이 폴더는 배포 때마다 비워지므로, `.env`와 카드 마스터는 **한 단계 위**에 둡니다.

```
/home/elaina/dockers/OpenVerseBE/
├── .env        ← 설정 (OVBE_DATA_DIR 포함)
├── data/       ← card_master_full.csv.gz
└── service/    ← CD가 매번 새로 복사 (직접 수정하지 않음)
```

처음 한 번만 준비합니다. 손으로 띄운 `~/OpenVerseBE`에서 옮기는 경우:

```bash
mkdir -p /home/elaina/dockers/OpenVerseBE
cp ~/OpenVerseBE/.env /home/elaina/dockers/OpenVerseBE/.env
cp -r ~/OpenVerseBE/data /home/elaina/dockers/OpenVerseBE/data
echo 'OVBE_DATA_DIR=/home/elaina/dockers/OpenVerseBE/data' >> /home/elaina/dockers/OpenVerseBE/.env
sed -i 's/^OPENVERSE_REF=.*/OPENVERSE_REF=main/' /home/elaina/dockers/OpenVerseBE/.env
```

compose 프로젝트 이름이 `openverse`로 고정되어 있어서, CD는 지금 떠 있는 컨테이너를 그대로 이어받고 Redis 볼륨(플레이어·viewer_id)도 유지합니다.

GitHub 저장소 Settings → Secrets and variables → Actions에 WebUI_BE와 같은 값으로 `SERVER_HOST`, `SERVER_USER`, `SERVER_PASSWORD`, `SERVER_PORT`를 넣습니다. 조직(Organization) 시크릿으로 이미 공유되어 있다면 따로 넣을 필요는 없습니다. 하나라도 비어 있으면 CD 첫 단계가 어떤 시크릿이 빠졌는지 알려 주고 멈춥니다.

Docker 없이 설치하는 방법은 [deploy/manual/README.md](deploy/manual/README.md)에 있습니다.

## 플레이어 쪽 설정

`openverse-launcher.exe` 옆에 두 파일을 둡니다.

- `be_url.txt`: `https://ov.example.com` (도메인을 WebUI와 같이 쓰면 `https://도메인/openverse`)
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
| `OVBE_MAX_ROOMS` | `0` (compose는 `10`) | 동시에 열 수 있는 방 개수. `0`이면 제한 없음 |
| `OVBE_TRUSTED_PROXIES` | (없음) | `X-Forwarded-For`를 믿을 프록시 IP(쉼표 구분). loopback은 항상 신뢰 |
