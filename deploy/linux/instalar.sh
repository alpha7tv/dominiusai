#!/usr/bin/env bash
# Instala ou atualiza o servidor do DOMINIUS PLAY AI CRM em uma VPS Linux (Ubuntu 22.04/24.04 ou Debian 12).
# Uso (como root):
#   bash instalar.sh                      -> acesso por http://IP:5080 (sem domínio)
#   bash instalar.sh crm.seudominio.com   -> nginx + HTTPS grátis (Let's Encrypt) no domínio
# Rodar de novo atualiza para a versão mais recente da main, sem apagar dados.
set -euo pipefail

DOMINIO="${1:-}"
EMAIL_SSL="${EMAIL_SSL:-}"                 # opcional: e-mail para avisos do Let's Encrypt
REPO="${REPO:-https://github.com/alpha7tv/dominiusai.git}"
BRANCH="${BRANCH:-main}"
APP_DIR=/opt/dominius-crm
DATA_DIR=/var/lib/dominius-crm
SRC_DIR=/opt/dominius-crm-src
DOTNET_DIR=/usr/share/dotnet
SVC=dominius-crm
PORTA="${PORTA:-5080}"

[ "$(id -u)" -eq 0 ] || { echo "Rode como root (sudo bash instalar.sh)"; exit 1; }

echo "==> Pacotes do sistema"
export DEBIAN_FRONTEND=noninteractive
# --allow-releaseinfo-change: PPAs de terceiros (ex.: ondrej/php) que mudaram o rótulo não bloqueiam a instalação
apt-get update -qq --allow-releaseinfo-change || echo "   (aviso: algum repositório do apt falhou; seguindo com os pacotes já conhecidos)"
apt-get install -y -qq curl git ca-certificates libicu-dev sqlite3 >/dev/null

echo "==> .NET 10 SDK (para compilar)"
tem_sdk10() { command -v dotnet >/dev/null && dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; }
[ -x "$DOTNET_DIR/dotnet" ] && export PATH="$DOTNET_DIR:$PATH"
if ! tem_sdk10; then
  # Ubuntu 24.04+ tem o pacote oficial; nas outras distribuições usa o instalador da Microsoft
  apt-get install -y -qq dotnet-sdk-10.0 >/dev/null 2>&1 || true
fi
if ! tem_sdk10; then
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$DOTNET_DIR" >/dev/null
  ln -sf "$DOTNET_DIR/dotnet" /usr/local/bin/dotnet
  export PATH="$DOTNET_DIR:$PATH" DOTNET_ROOT="$DOTNET_DIR"
fi
tem_sdk10 || { echo "Não consegui instalar o .NET 10 SDK"; exit 1; }
export DOTNET_CLI_TELEMETRY_OPTOUT=1

echo "==> Código-fonte ($BRANCH)"
if [ -d "$SRC_DIR/.git" ]; then
  git -C "$SRC_DIR" fetch -q origin "$BRANCH" && git -C "$SRC_DIR" checkout -q -B "$BRANCH" FETCH_HEAD
else
  git clone -q -b "$BRANCH" "$REPO" "$SRC_DIR"
fi

case "$(uname -m)" in aarch64|arm64) RID=linux-arm64 ;; *) RID=linux-x64 ;; esac
echo "==> Compilando servidor ($RID)"
rm -rf /tmp/dominius-pub
dotnet publish "$SRC_DIR/src/DominiusPlay.Server" -c Release -r "$RID" --self-contained true -o /tmp/dominius-pub -v quiet -nologo

echo "==> Usuário e pastas"
id -u dominius >/dev/null 2>&1 || useradd --system --home "$DATA_DIR" --shell /usr/sbin/nologin dominius
mkdir -p "$DATA_DIR" "$DATA_DIR/backups"
chown -R dominius:dominius "$DATA_DIR"; chmod 750 "$DATA_DIR"

# Backup do banco antes de trocar a versão
if [ -f "$DATA_DIR/crm.db" ]; then
  sqlite3 "$DATA_DIR/crm.db" ".backup '$DATA_DIR/backups/antes-atualizar-$(date +%Y%m%d-%H%M%S).db'" && echo "    backup do banco feito"
fi

# VPS compartilhada: não mexe em nada que já esteja usando as portas
systemctl stop "$SVC" 2>/dev/null || true; sleep 1
if ss -ltn "( sport = :$PORTA )" | grep -q LISTEN; then
  echo "!! A porta $PORTA já está em uso por outro serviço:"; ss -ltnp "( sport = :$PORTA )"
  echo "   Rode de novo escolhendo outra porta, ex.: PORTA=5090 bash instalar.sh $DOMINIO"; exit 1
fi
if [ -n "$DOMINIO" ] && ! command -v nginx >/dev/null && ss -ltn '( sport = :80 or sport = :443 )' | grep -q LISTEN; then
  echo "!! As portas 80/443 já estão ocupadas por outro programa (não é nginx):"; ss -ltnp '( sport = :80 or sport = :443 )'
  echo "   Aponte o proxy que já existe para http://127.0.0.1:$PORTA, ou rode sem domínio."; exit 1
fi
mkdir -p "$APP_DIR"
# Preserva appsettings.json já editado (ex.: PostgreSQL) em atualizações
[ -f "$APP_DIR/appsettings.json" ] && cp "$APP_DIR/appsettings.json" /tmp/dominius-appsettings.bak
rm -rf "$APP_DIR"/*; cp -a /tmp/dominius-pub/. "$APP_DIR/"
[ -f /tmp/dominius-appsettings.bak ] && mv /tmp/dominius-appsettings.bak "$APP_DIR/appsettings.json"
chown -R root:root "$APP_DIR"; chmod +x "$APP_DIR/DominiusPlay.Server"

# Com domínio o servidor só escuta localmente (o nginx atende a internet); sem domínio, escuta em todas as interfaces
if [ -n "$DOMINIO" ]; then LISTEN="http://127.0.0.1:$PORTA"; else LISTEN="http://0.0.0.0:$PORTA"; fi

echo "==> Serviço systemd"
cat > /etc/systemd/system/$SVC.service <<UNIT
[Unit]
Description=DOMINIUS PLAY AI CRM (servidor)
After=network-online.target
Wants=network-online.target

[Service]
User=dominius
Group=dominius
WorkingDirectory=$APP_DIR
ExecStart=$APP_DIR/DominiusPlay.Server
Environment=DataDirectory=$DATA_DIR
Environment=Urls=$LISTEN
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
Restart=always
RestartSec=5
KillSignal=SIGINT
TimeoutStopSec=30
UMask=0077
NoNewPrivileges=true
ProtectSystem=full
ReadWritePaths=$DATA_DIR

[Install]
WantedBy=multi-user.target
UNIT
systemctl daemon-reload
systemctl enable --now "$SVC" >/dev/null

echo -n "==> Aguardando o servidor"
for i in $(seq 1 30); do curl -fs "http://127.0.0.1:$PORTA/health" >/dev/null && break; echo -n "."; sleep 1; done; echo
curl -fs "http://127.0.0.1:$PORTA/health" || { echo "Servidor não respondeu. Veja: journalctl -u $SVC -n 80"; exit 1; }
echo

if [ -n "$DOMINIO" ]; then
  echo "==> nginx + HTTPS para $DOMINIO"
  apt-get install -y -qq nginx certbot python3-certbot-nginx >/dev/null
  cat > /etc/nginx/sites-available/$SVC <<NGINX
map \$http_upgrade \$dominius_connection_upgrade { default upgrade; '' close; }
server {
    listen 80;
    server_name $DOMINIO;
    client_max_body_size 50m;   # importação de planilhas de leads
    location / {
        proxy_pass http://127.0.0.1:$PORTA;
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_set_header Upgrade \$http_upgrade;        # SignalR (tempo real)
        proxy_set_header Connection \$dominius_connection_upgrade;
        proxy_read_timeout 120s;
    }
}
NGINX
  ln -sf /etc/nginx/sites-available/$SVC /etc/nginx/sites-enabled/$SVC
  nginx -t -q && systemctl reload nginx
  if [ ! -d "/etc/letsencrypt/live/$DOMINIO" ]; then
    if [ -n "$EMAIL_SSL" ]; then EM=(-m "$EMAIL_SSL"); else EM=(--register-unsafely-without-email); fi
    certbot --nginx -d "$DOMINIO" --non-interactive --agree-tos --redirect "${EM[@]}" \
      || echo "!! Certificado não emitido. Confira se o DNS de $DOMINIO aponta para este IP e rode o script de novo."
  fi
  if [ -d "/etc/letsencrypt/live/$DOMINIO" ]; then URL="https://$DOMINIO"; else URL="http://$DOMINIO"; fi
else
  URL="http://$(curl -fs4 https://api.ipify.org || hostname -I | awk '{print $1}'):$PORTA"
fi

if command -v ufw >/dev/null && ufw status | grep -q "Status: active"; then
  ufw allow OpenSSH >/dev/null
  if [ -n "$DOMINIO" ]; then ufw allow 'Nginx Full' >/dev/null; else ufw allow $PORTA/tcp >/dev/null; fi
fi

# Backup diário do banco às 03:30, mantém 14 dias
cat > /etc/cron.d/$SVC-backup <<CRON
30 3 * * * dominius [ -f $DATA_DIR/crm.db ] && sqlite3 $DATA_DIR/crm.db ".backup '$DATA_DIR/backups/diario-\$(date +\%Y\%m\%d).db'" && find $DATA_DIR/backups -name 'diario-*.db' -mtime +14 -delete
CRON

cat <<FIM

============================================================
 DOMINIUS PLAY AI CRM instalado.
 Endereço do servidor:  $URL
 Dados (banco, chaves): $DATA_DIR   <- faça backup desta pasta

 No aplicativo Windows, na tela de login, troque o servidor para:
   $URL
 Em Integrações, use como "URL pública deste servidor":
   $URL

 Comandos úteis:
   systemctl status $SVC        (situação)
   journalctl -u $SVC -f        (logs ao vivo)
   systemctl restart $SVC       (reiniciar)
   bash $SRC_DIR/deploy/linux/instalar.sh $DOMINIO   (atualizar)
============================================================
FIM
