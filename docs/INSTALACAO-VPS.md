# Instalar o servidor do DOMINIUS PLAY AI CRM em uma VPS Linux

O CRM tem duas partes:

| Parte | Onde roda |
|---|---|
| **Servidor** (API, banco, IA, filas, webhook do WhatsApp) | Na VPS Linux, 24 horas por dia |
| **Aplicativo** (as telas) | No(s) computador(es) Windows dos atendentes, conectado ao servidor pela internet |

A **Evolution API** (WhatsApp) é um sistema separado. Se você já tem uma Evolution rodando, não precisa instalar outra: o CRM só precisa da URL, da instância e da chave dela.

## Requisitos

- VPS com **Ubuntu 22.04, Ubuntu 24.04 ou Debian 12**, 64 bits (x64 ou ARM).
- Mínimo **1 vCPU e 2 GB de RAM** (a compilação usa ~1,5 GB; o servidor rodando usa ~200 MB). 20 GB de disco.
- Acesso **root por SSH**.
- Recomendado: um **subdomínio** (ex.: `crm.seudominio.com`) com registro **A** apontando para o IP da VPS. Com domínio o script coloca **HTTPS grátis** (Let's Encrypt). Sem domínio funciona por `http://IP:5080`, sem criptografia.

## 1. Apontar o domínio (se for usar)

No painel do seu DNS (Cloudflare, Registro.br, Hostinger...) crie:

```
Tipo: A    Nome: crm    Valor: IP_DA_VPS    (Cloudflare: nuvem cinza / "DNS only")
```

Espere alguns minutos e confira no seu PC: `ping crm.seudominio.com` deve mostrar o IP da VPS.

## 2. Instalar

Entre na VPS por SSH e rode **um** dos comandos:

```bash
# Com domínio (recomendado)
curl -fsSL https://raw.githubusercontent.com/alpha7tv/dominiusai/main/deploy/linux/instalar.sh -o instalar.sh
sudo EMAIL_SSL=seuemail@exemplo.com bash instalar.sh crm.seudominio.com

# Sem domínio (acesso por IP:5080)
curl -fsSL https://raw.githubusercontent.com/alpha7tv/dominiusai/main/deploy/linux/instalar.sh -o instalar.sh
sudo bash instalar.sh
```

> Se o repositório for privado, o `curl`/`git clone` pede autenticação. Nesse caso clone com um token:
> `git clone https://SEU_TOKEN@github.com/alpha7tv/dominiusai.git /opt/dominius-crm-src` e rode
> `sudo REPO=https://SEU_TOKEN@github.com/alpha7tv/dominiusai.git bash /opt/dominius-crm-src/deploy/linux/instalar.sh crm.seudominio.com`.

O script (5 a 10 minutos na primeira vez):

1. Instala as dependências e o .NET 10 SDK.
2. Baixa o código e compila o servidor para Linux.
3. Cria o usuário de sistema `dominius` e a pasta de dados `/var/lib/dominius-crm`.
4. Cria o serviço `dominius-crm` (systemd): sobe junto com a VPS e reinicia sozinho se cair.
5. Com domínio: instala o **nginx** como proxy reverso (com suporte ao tempo real/SignalR) e emite o certificado HTTPS. O servidor fica escutando só em `127.0.0.1:5080`, e a internet entra pelo nginx (portas 80/443).
6. Libera as portas no firewall `ufw` (se estiver ativo) e agenda **backup diário** do banco às 03:30 (mantém 14 dias).

No fim ele mostra o endereço do servidor. Teste no navegador: `https://crm.seudominio.com/health` deve responder `{"status":"ok",...}`.

### Firewall do provedor

Alguns provedores (AWS, Oracle, Google Cloud, Hostinger, Contabo com firewall) têm firewall no painel. Libere:
- Com domínio: portas **80** e **443** (TCP).
- Sem domínio: porta **5080** (TCP).

## 3. Conectar o aplicativo Windows

1. Abra o DOMINIUS PLAY AI CRM no Windows.
2. Na tela de login, no campo **Servidor**, coloque `https://crm.seudominio.com` (ou `http://IP:5080`).
3. No primeiro acesso ele pede para criar o **usuário administrador** (o banco da VPS começa vazio).
4. Os outros atendentes usam o mesmo endereço, cada um com seu usuário (tela Equipe).

> Se você já usa o CRM instalado no Windows e quer **levar os dados**, veja "Migrar dados do Windows" abaixo antes de criar o administrador.

## 4. Configurar IA e WhatsApp

1. **Configurações de IA**: cole a chave da Anthropic (fica criptografada no servidor da VPS).
2. **Integrações**: URL da Evolution API, instância e chave. Em **URL pública deste servidor** coloque `https://crm.seudominio.com`.
3. Clique em **Testar conexão** e depois **Registrar webhook**. A Evolution passa a enviar as mensagens para `https://crm.seudominio.com/webhooks/evolution`.

A Evolution precisa conseguir acessar a VPS pela internet (ou pela rede interna, se estiverem na mesma máquina/rede).

## Migrar dados do Windows (opcional)

No Windows, os dados ficam em `C:\ProgramData\DominiusPlay\CRM`. Para levar tudo (leads, conversas, configurações e chaves):

1. No Windows: pare o serviço `DominiusPlayCRM` (services.msc) para fechar o banco.
2. Copie `crm.db`, `jwt.key` e a pasta `keys` para a VPS (ex.: com WinSCP) em `/tmp/migracao/`.
3. Na VPS:
   ```bash
   sudo systemctl stop dominius-crm
   sudo rm -f /var/lib/dominius-crm/crm.db-wal /var/lib/dominius-crm/crm.db-shm
   sudo cp /tmp/migracao/crm.db /tmp/migracao/jwt.key /var/lib/dominius-crm/
   sudo rm -rf /var/lib/dominius-crm/keys && sudo cp -r /tmp/migracao/keys /var/lib/dominius-crm/
   sudo chown -R dominius:dominius /var/lib/dominius-crm
   sudo systemctl start dominius-crm
   ```
4. **Importante:** a pasta `keys` é o que permite ler a chave da Anthropic e a da Evolution guardadas criptografadas. Se ela não for junto, cole as chaves de novo em Configurações de IA e Integrações.
5. Atualize a **URL pública** em Integrações e clique em **Registrar webhook** de novo.

## Atualizar para uma versão nova

```bash
sudo bash /opt/dominius-crm-src/deploy/linux/instalar.sh crm.seudominio.com
```

Ele faz backup do banco antes (`/var/lib/dominius-crm/backups/antes-atualizar-*.db`), compila a versão nova da `main` e reinicia. Dados e `appsettings.json` editado são preservados.

## Comandos do dia a dia

| Para | Comando |
|---|---|
| Ver se está rodando | `systemctl status dominius-crm` |
| Logs ao vivo | `journalctl -u dominius-crm -f` |
| Últimos erros | `journalctl -u dominius-crm -p warning -n 100` |
| Reiniciar | `sudo systemctl restart dominius-crm` |
| Backup manual | `sudo sqlite3 /var/lib/dominius-crm/crm.db ".backup '/root/crm-$(date +%F).db'"` |
| Renovar HTTPS | automático (certbot); teste com `sudo certbot renew --dry-run` |

## Restaurar um backup

```bash
sudo systemctl stop dominius-crm
sudo rm -f /var/lib/dominius-crm/crm.db-wal /var/lib/dominius-crm/crm.db-shm
sudo cp /var/lib/dominius-crm/backups/ARQUIVO.db /var/lib/dominius-crm/crm.db
sudo chown dominius:dominius /var/lib/dominius-crm/crm.db
sudo systemctl start dominius-crm
```

Guarde também uma cópia da pasta `/var/lib/dominius-crm/keys` fora da VPS: sem ela as chaves salvas não podem ser lidas.

## PostgreSQL (opcional)

O SQLite atende bem uma equipe pequena no mesmo servidor. Para PostgreSQL, edite `/opt/dominius-crm/appsettings.json`:

```json
"Database": { "Provider": "Postgres" },
"ConnectionStrings": { "Crm": "Host=localhost;Database=dominius_crm;Username=dominius;Password=SENHA" }
```

e rode `sudo systemctl restart dominius-crm`. As atualizações preservam esse arquivo. O backup diário automático cobre só o SQLite; com PostgreSQL use `pg_dump`.

## Onde fica cada coisa

| Caminho | Conteúdo |
|---|---|
| `/opt/dominius-crm` | Servidor compilado (substituído a cada atualização) |
| `/opt/dominius-crm-src` | Código-fonte usado para compilar |
| `/var/lib/dominius-crm` | **Dados**: `crm.db`, `jwt.key`, `keys/`, `backups/` |
| `/etc/systemd/system/dominius-crm.service` | Serviço |
| `/etc/nginx/sites-available/dominius-crm` | Proxy reverso |
