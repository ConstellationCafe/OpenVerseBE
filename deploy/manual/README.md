# Docker 없이 설치 (Ubuntu 22.04)

보통은 저장소 루트의 `docker-compose.yml`을 쓰세요. 이 문서는 systemd로 직접 운영할 때의 절차입니다.

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

`/etc/redis/redis.conf`에서 [`deploy/manual/redis-openverse.conf`](redis-openverse.conf)의 항목을 맞춘 뒤:

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
sudo cp ~/OpenVerseBE/deploy/manual/openverse.env.example /etc/openverse/openverse.env
sudo nano /etc/openverse/openverse.env      # 비밀번호, 도메인, 카페 키 수정
sudo chown root:openverse /etc/openverse/openverse.env && sudo chmod 640 /etc/openverse/openverse.env

sudo cp ~/OpenVerseBE/deploy/manual/systemd/*.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now openversebe openverse-battle

sudo cp ~/OpenVerseBE/deploy/manual/Caddyfile /etc/caddy/Caddyfile
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

