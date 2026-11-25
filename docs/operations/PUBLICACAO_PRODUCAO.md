# Plano de publicação da versão multiagência em produção

Atualizado em 25/11/2026. Decisão do dono: a produção vai para o **servidor próprio** (notebook Ubuntu, Docker, Cloudflare Tunnel), usando `deploy/servidor/`. O Railway fica como está até a virada e serve de volta atrás.

Nada deste plano foi executado em produção. Merge na `main` e a virada dependem de aprovação explícita do dono.

## 1. Ambientes

| Ambiente | Onde | Código | Banco |
|---|---|---|---|
| Produção atual | Railway (deploy da `main`) | `751be85`, agência única | Postgres do Railway |
| Produção nova | Servidor próprio (`deploy/servidor`) | `feature/saas-multitenant` (depois `main`) | Postgres 17 no Docker do servidor |
| Homologação | Render free (`render.yaml`) | `feature/saas-multitenant` | Neon |
| Local | `http://localhost:5159` | `feature/saas-multitenant` | Docker Postgres 5434 |

Vantagem da troca: a migração roda numa cópia dos dados no servidor novo. O banco do Railway só é lido (backup), nunca alterado.

## 2. O que muda nos dados

As migrations rodam sozinhas na subida (`SeedInicial` chama `MigrateAsync`). No servidor novo isso acontece sobre a cópia restaurada.

- É criada a agência 1 com o nome de Configurações; todos os registros passam a pertencer a ela.
- A agência 1 (Social Unânime) fica isenta de assinatura e Ativa.
- Todos os usuários veem o aceite dos Termos no próximo acesso.
- Arquivos antigos (`wwwroot/uploads`) deixam de ser públicos e precisam ir para o armazenamento novo com `--migrar-arquivos`.
- O login passa a exigir e-mail confirmado. A versão antiga criava usuários já confirmados; conferir mesmo assim com `SELECT count(*) FROM "AspNetUsers" WHERE NOT "EmailConfirmed"`.

## 3. Variáveis no `.env` do servidor

| Variável | Para quê | Situação |
|---|---|---|
| `AMBIENTE=Production` | regras de produção | ok |
| `POSTGRES_PASSWORD` | banco local | definir senha forte |
| `APP_BASE_URL` | links de e-mail | endereço público do túnel (https) |
| `SUPERADMIN_EMAIL`, `SUPERADMIN_PASSWORD` | painel da plataforma (12+ caracteres) | definir |
| `EMAIL_PROVIDER=resend`, `RESEND_API_KEY`, `EMAIL_FROM` | convites, confirmação, senha | pendente: conta Resend (com `log` nada é enviado) |
| `STORAGE_PROVIDER` | arquivos | `local` (volume `storage`, entra no backup) ou R2 |
| `PAYMENT_GATEWAY` | cobrança | `desabilitado` no início |
| `CLOUDFLARE_TUNNEL_TOKEN` | acesso público | pendente: criar túnel e domínio |
| `SENTRY_DSN` | erros | opcional |

## 4. Passo a passo

1. **Backup do Railway (somente leitura).** `pg_dump -Fc` com a `DATABASE_URL` pública do Railway, com manifesto de contagens e sha256 (`scripts/backup/backup-postgres.sh`).
2. **Arquivos do Railway.** Verificar se existe volume com `wwwroot/uploads`. Se o disco for efêmero, os arquivos antigos já se perderam a cada deploy e só os do banco existem; registrar o que houver.
3. **Teste de restauração** local (`scripts/backup/testar-restauracao.sh`), comparando o manifesto.
4. **Ensaio local.** Subir a branch sobre a cópia, conferir com `docs/architecture/scripts/validar-migracao-tenant.sql` (nenhum registro sem agência, contagens iguais) e entrar com um usuário real.
5. **Preparar o servidor.** Clonar o repositório em `/srv/apps/AgenciaOS` na branch aprovada, preencher `deploy/servidor/.env`, `docker compose up -d postgres`.
6. **Restaurar o dump no Postgres do servidor** (`pg_restore --no-owner`), subir o app (`docker compose up -d --build`); as migrations rodam na subida. Conferir `/health/ready` e os logs.
7. **Migrar arquivos** com `--migrar-arquivos` e validar login, clientes, tarefas, aprovações, financeiro e o painel `/Plataforma`.
8. **Aprovação do dono → virada.** Congelar o uso do Railway, repetir 1 e 6 com um dump final (para não perder o que foi feito no meio), ligar o túnel (`docker compose --profile tunnel up -d`) e apontar o domínio.
9. **Merge na `main`** da `feature/saas-multitenant`, para o código de produção ficar na branch principal. Antes, desligar o deploy automático do Railway para ele não publicar a versão nova sobre o banco antigo.
10. **Backup diário** no servidor: `deploy/servidor/backup.sh` no cron (03:00), para um HD separado, com teste de restauração mensal. Se possível, cópia fora de casa (R2).

## 5. Volta atrás

- Até o passo 8, a produção continua no Railway sem mudança nenhuma.
- Depois da virada: desligar o túnel e voltar a usar o Railway (código e banco intactos). O que foi feito no servidor novo depois da virada não volta sozinho para o Railway.
- Manter o Railway ligado por algumas semanas antes de cancelar.

## 6. Riscos do servidor próprio

- Queda de energia ou internet derruba o sistema: usar nobreak e `restart: unless-stopped` (já configurado).
- Disco do notebook: backup diário em outro disco e cópia externa.
- Segurança: banco não exposto na rede (já configurado); manter Ubuntu e Docker atualizados; acesso de fora só pelo túnel.

## 7. O que preciso do dono

1. A `DATABASE_URL` pública do Postgres do Railway (colada no terminal local, nunca no chat), para o backup.
2. Saber se o servidor Ubuntu já está com Docker instalado e como acessá-lo (SSH ou Tailscale).
3. Domínio para o túnel e conta Resend para e-mail.
4. Aprovação explícita da virada, com data e horário.
