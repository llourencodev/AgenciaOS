# Como hospedar o Midia+ no servidor Ubuntu

Guia para o notebook Ubuntu com Docker. Os comandos rodam no terminal do servidor. Onde aparece `SEU_...`, troque pelo seu valor.

O sistema roda em três peças, todas em Docker: o **banco** (Postgres), o **app** (Midia+) e o **túnel** da Cloudflare, que publica o site na internet sem abrir portas no roteador.

---

## Parte 1. Preparar o notebook (uma vez só)

**1. Atualizar o sistema e conferir o Docker**

```bash
sudo apt update && sudo apt upgrade -y
sudo apt install -y git
docker --version && docker compose version
```

Se `docker compose` der erro, instale o plugin: `sudo apt install -y docker-compose-plugin`.

**2. Não deixar o notebook dormir com a tampa fechada**

```bash
sudo sed -i 's/^#\?HandleLidSwitch=.*/HandleLidSwitch=ignore/' /etc/systemd/logind.conf
sudo sed -i 's/^#\?HandleLidSwitchExternalPower=.*/HandleLidSwitchExternalPower=ignore/' /etc/systemd/logind.conf
sudo systemctl restart systemd-logind
```

Também vale desligar a suspensão automática nas configurações de energia e deixar o notebook ligado num nobreak.

**3. IP fixo na rede de casa**

No roteador, reserve um IP para o notebook (hoje ele usa `192.168.0.107`). Assim o endereço não muda.

**4. Atualizações de segurança automáticas e firewall**

```bash
sudo apt install -y unattended-upgrades
sudo dpkg-reconfigure -plow unattended-upgrades
sudo ufw allow OpenSSH
sudo ufw allow from 192.168.0.0/24 to any port 8090
sudo ufw enable
```

A porta 8090 fica aberta só para a rede de casa. Para a internet, o acesso é pelo túnel.

**5. Pastas**

```bash
sudo mkdir -p /srv/apps /srv/backup/midiaplus
sudo chown -R $USER /srv/apps /srv/backup
```

Se tiver um segundo HD, monte-o em `/srv/backup` para os backups não ficarem no mesmo disco do sistema.

---

## Parte 2. Baixar o sistema

**6. Clonar o repositório**

O repositório é privado. Crie um token no GitHub (Settings → Developer settings → Personal access tokens → Fine-grained, só leitura do repositório AgenciaOS) e use-o como senha quando o git pedir.

```bash
cd /srv/apps
git clone https://github.com/llourencodev/AgenciaOS.git
cd AgenciaOS
git checkout feature/saas-multitenant
```

(Depois da aprovação e do merge, a branch passa a ser a `main`.)

---

## Parte 3. Configurar

**7. Criar o arquivo de configuração `.env`**

```bash
cd /srv/apps/AgenciaOS/deploy/servidor
cp .env.example .env
openssl rand -base64 32 | tr -d '/+='     # copie o resultado: é a senha do banco
nano .env
```

Preencha no `.env`:

| Campo | O que colocar |
|---|---|
| `POSTGRES_PASSWORD` | a senha gerada acima |
| `APP_BASE_URL` | por enquanto `http://192.168.0.107:8090`; depois do túnel, o endereço `https://...` |
| `SUPERADMIN_EMAIL` | seu e-mail de dono da plataforma |
| `SUPERADMIN_PASSWORD` | senha forte, 12+ caracteres |
| `EMAIL_PROVIDER` | `log` por enquanto (não envia e-mail); depois `resend` com a chave |
| `STORAGE_PROVIDER` | `local` (arquivos ficam no disco do servidor e entram no backup) |
| `PAYMENT_GATEWAY` | `desabilitado` por enquanto |

Salve com Ctrl+O, Enter, Ctrl+X. Esse arquivo tem senhas: nunca mande para o git nem por mensagem.

```bash
chmod 600 .env
```

---

## Parte 4. Trazer os dados do Railway

Esta parte eu conduzo com você. Resumo:

**8. Backup do Railway.** Faço um `pg_dump` só de leitura do banco do Railway e testo a restauração antes. O Railway não é alterado.

**9. Restaurar no servidor, antes de ligar o app pela primeira vez.** Copie o arquivo `.dump` para `/srv/backup/midiaplus/` e rode:

```bash
cd /srv/apps/AgenciaOS/deploy/servidor
docker compose up -d postgres
docker compose exec -T postgres sh -c 'pg_restore -U "$POSTGRES_USER" -d "$POSTGRES_DB" --no-owner --no-privileges' < /srv/backup/midiaplus/railway.dump
```

---

## Parte 5. Ligar o sistema

**10. Subir banco e app**

```bash
cd /srv/apps/AgenciaOS/deploy/servidor
docker compose up -d --build
docker compose logs -f app        # Ctrl+C para sair dos logs
```

Na primeira subida o app atualiza o banco sozinho (cria a estrutura de agências e coloca todos os dados na Social Unânime). Espere aparecer que está escutando na porta 8080.

**11. Conferir**

```bash
curl http://localhost:8090/health/ready
```

Depois abra `http://192.168.0.107:8090` em outro computador da casa e entre com um usuário real e com o superadmin (painel em `/Plataforma`).

**12. Arquivos antigos** (anexos e logos enviados antes). Eles ficam no disco do Railway, não no banco. Se conseguirmos copiá-los de lá no passo 8, eu te passo o comando para colocá-los no servidor e rodar a migração (`--migrar-arquivos`), que os guarda no armazenamento privado novo.

---

## Parte 6. Colocar na internet (Cloudflare Tunnel)

**13. Domínio na Cloudflare.** O domínio (ex.: `midiaplus.com.br`) precisa estar com o DNS na Cloudflare (plano grátis serve).

**14. Criar o túnel.** No painel da Cloudflare: Zero Trust → Networks → Tunnels → Create a tunnel → Cloudflared. Dê um nome (ex.: `midiaplus`) e copie o **token** que aparece no comando de instalação (o texto longo depois de `--token`).

**15. Apontar o endereço.** Ainda no túnel, em Public Hostname: subdomínio `app` (ou o que quiser), domínio `midiaplus.com.br`, serviço `HTTP` e URL `app:8080`.

**16. Ligar o túnel no servidor.** No `.env`, preencha `CLOUDFLARE_TUNNEL_TOKEN` com o token e troque `APP_BASE_URL` para `https://app.midiaplus.com.br`. Depois:

```bash
docker compose --profile tunnel up -d
```

Pronto: o sistema abre em `https://app.midiaplus.com.br`, com HTTPS da Cloudflare, sem abrir portas no roteador.

---

## Parte 7. Backup diário

**17. Agendar**

```bash
crontab -e
```

Adicione a linha:

```
0 3 * * * sh /srv/apps/AgenciaOS/deploy/servidor/backup.sh >> /srv/backup/midiaplus/backup.log 2>&1
```

Todo dia às 3h ele salva o banco e os arquivos em `/srv/backup/midiaplus`, guardando 30 dias. Uma vez por mês, teste restaurar um backup. Se puder, copie os backups também para fora de casa (Google Drive, R2 ou HD externo guardado em outro lugar).

---

## Dia a dia

| Para | Comando (dentro de `deploy/servidor`) |
|---|---|
| Ver se está rodando | `docker compose ps` |
| Ver os logs | `docker compose logs -f app` |
| Reiniciar | `docker compose restart app` |
| Atualizar para uma versão nova | `sh backup.sh` e depois `git pull && docker compose up -d --build` |
| Desligar tudo | `docker compose --profile tunnel down` (os dados ficam guardados) |

Nunca use `docker compose down -v`: o `-v` apaga o banco e os arquivos.

Depois de queda de energia, tudo volta sozinho quando o notebook ligar (os serviços estão com reinício automático), desde que o Docker inicie com o sistema: `sudo systemctl enable docker`.
