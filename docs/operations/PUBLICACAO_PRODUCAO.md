# Plano de publicação da versão multiagência em produção

Situação em 24/11/2026. Nada deste plano foi executado em produção. Merge na `main` e publicação dependem de aprovação explícita do dono.

## 1. Onde está cada ambiente hoje

| Ambiente | Onde | Código | Banco |
|---|---|---|---|
| Produção | Railway (deploy da `main`, `railway.toml`) | `origin/main` = `751be85` (versão de agência única) | Postgres do Railway |
| Homologação | Render free (`render.yaml`, serviço `agenciaos-teste`) | `feature/saas-multitenant` | Neon |
| Local | `http://localhost:5159` | `feature/saas-multitenant` | Docker Postgres 5434 |
| Servidor próprio (em preparo) | `deploy/servidor/` (notebook Ubuntu + Cloudflare Tunnel) | não versionado ainda | Postgres no Docker |

Diferença entre `main` e a branch: 208 arquivos, 6 migrations novas, todas aditivas.

Pontos a confirmar com o dono:
- A produção continua no Railway, ou vai migrar para o servidor próprio (`deploy/servidor`)? O plano abaixo vale para os dois; muda só onde rodar os comandos.
- A branch `origin/railway/fix-deploy-6b6325` (correções automáticas do Railway de junho) nunca entrou na `main`. Precisa decidir se é descartada.

## 2. O que muda ao publicar

As migrations rodam sozinhas na subida (`SeedInicial` chama `MigrateAsync`). Ou seja, publicar = migrar o banco de produção. Por isso o backup vem antes de tudo.

Efeitos nos dados existentes:
- É criada a agência 1 com o nome que está em Configurações; todos os registros atuais passam a pertencer a ela.
- A agência 1 (Social Unânime) fica isenta de assinatura e com situação Ativa.
- Todos os usuários verão a tela de aceite dos Termos no próximo acesso.
- Arquivos antigos em `wwwroot/uploads` deixam de ser servidos publicamente; precisam ser migrados com `--migrar-arquivos`.
- O login passa a exigir e-mail confirmado. A versão antiga criava usuários já confirmados, mas no ensaio (passo 3) conferir `SELECT count(*) FROM "AspNetUsers" WHERE NOT "EmailConfirmed"`; se houver algum, confirmar antes de publicar para ninguém ficar sem acesso.

## 3. Variáveis obrigatórias na produção

Sem elas a aplicação recusa subir em Production ou desabilita o recurso:

| Variável | Para quê | Situação |
|---|---|---|
| `APP_BASE_URL` | links de e-mail | definir |
| `SUPERADMIN_EMAIL`, `SUPERADMIN_PASSWORD` | painel da plataforma (12+ caracteres) | definir |
| `EMAIL_PROVIDER=resend`, `RESEND_API_KEY`, `EMAIL_FROM` | convites, confirmação, senha | pendente: conta Resend |
| `STORAGE_PROVIDER=s3`, `S3_*` | arquivos privados | pendente: bucket R2 (ou `local` com volume persistente) |
| `PAYMENT_GATEWAY`, `ASAAS_API_KEY`, `ASAAS_WEBHOOK_TOKEN` | cobrança | pode ficar `desabilitado` no início |
| `PLATAFORMA_HOSTS`, `PLATAFORMA_DOMINIO_BASE`, `PLATAFORMA_CNAME_ALVO` | domínios próprios | opcional no início |
| `SENTRY_DSN` | erros | opcional |

Atenção: no Railway o disco é efêmero. Com `STORAGE_PROVIDER=local` os arquivos somem a cada deploy, a menos que exista um volume montado.

## 4. Passo a passo

1. **Backup da produção (somente leitura).** `scripts/backup/backup-postgres.sh` com a `DATABASE_URL` pública do Railway. Gera `.dump`, manifesto e sha256.
2. **Teste de restauração.** `scripts/backup/testar-restauracao.sh` restaura o dump num banco separado local e compara o manifesto.
3. **Ensaio da migração na cópia.** Subir a branch apontando para a cópia restaurada; conferir com `docs/architecture/scripts/validar-migracao-tenant.sql` (nenhum registro sem agência, contagens iguais antes/depois) e entrar com um usuário real da cópia.
4. **Configurar variáveis** do item 3 no serviço de produção (sem publicar ainda).
5. **Aprovação do dono.** Só então: merge de `feature/saas-multitenant` na `main` (com CI verde).
6. **Publicação.** O Railway publica a `main`; as migrations rodam na subida. Acompanhar `/health/ready` e os logs.
7. **Pós-publicação.** Rodar `--migrar-arquivos` para os uploads antigos; validar login, clientes, tarefas, aprovações e financeiro da Social Unânime; conferir o painel `/Plataforma`.
8. **Ativar o backup diário** (`.github/workflows/backup.yml`) com os segredos `PROD_DATABASE_URL` e `BACKUP_*`.

## 5. Volta atrás (rollback)

- Falha antes do passo 6: nada mudou em produção.
- Falha depois: reverter a `main` para `751be85` no Railway e restaurar o dump do passo 1 (`pg_restore --clean`). Dados criados entre a publicação e a volta são perdidos; por isso a janela deve ser num horário sem uso.
- Tempo estimado de volta: 30 minutos. RPO: o momento do backup do passo 1.

## 6. O que preciso do dono

1. Confirmar onde fica a produção (Railway ou servidor próprio).
2. Acesso somente leitura ao banco de produção para o backup (a `DATABASE_URL` pública do Railway, colada direto no terminal local, nunca no chat).
3. Decidir o armazenamento de arquivos (R2 ou volume) e o e-mail (Resend).
4. Aprovação explícita para o merge e a publicação, com horário.
