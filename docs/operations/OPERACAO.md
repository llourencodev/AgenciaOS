# Operação do AgenciaOS (Fase 6)

Guia de ambientes, monitoramento, backups, recuperação de desastre e CI/CD. Nada aqui foi aplicado no Railway ainda: cada item marcado com **(ação)** precisa ser feito pelo proprietário.

## 1. Ambientes

| | Desenvolvimento | Homologação (Staging) | Produção |
|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Development` | `Staging` | `Production` |
| Onde | PC local (Docker Postgres 5434) | Railway, ambiente separado do projeto | Railway |
| Banco | local | **outro** Postgres | Postgres de produção |
| Arquivos | `App_Data/storage` (local) | bucket R2 próprio (`agenciaos-hmg`) | bucket R2 de produção |
| E-mail | `EMAIL_PROVIDER=log` | Resend com domínio/remetente de teste | Resend |
| Pagamentos | gateway simulado | **Asaas sandbox** (`$aact_hmlg_...`) | Asaas produção |
| Dados | fictícios | cópia **anonimizada** de produção ou fictícios | reais |

Regras no código: homologação segue as mesmas proteções de produção (`EhProducaoOuHomologacao`): gateway simulado proibido, `APP_BASE_URL` obrigatório para links de e-mail, proxy confiável para IP real, avisos de configuração no log.

**(ação)** No Railway: *Project → Environments → New Environment "staging"*, com variáveis e banco próprios. Nunca copiar as variáveis de produção para lá.

### Cópia de produção para homologação
1. Restaurar o backup mais recente no banco de homologação (`pg_restore`).
2. Anonimizar: `psql "$HMG_DATABASE_URL" -v ON_ERROR_STOP=1 -c "SET agenciaos.confirmar_anonimizacao = 'sim';" -f scripts/homologacao/anonimizar-copia.sql`
   - troca nomes, e-mails, telefones, CNPJ, textos livres; apaga senhas, chaves de 2FA, tokens de integração, chaves de criptografia, convites e notificações; troca ids do gateway.
   - recusa rodar sem a confirmação e em bancos com "prod" no nome (testado localmente).
3. Para entrar: `dotnet AgenciaOS.dll --definir-senha usuario-xxxxxxxx@homologacao.invalid <senha>` (o comando é recusado em produção).

## 2. Variáveis de ambiente (produção)

| Variável | Para quê |
|---|---|
| `ConnectionStrings__DefaultConnection` | banco |
| `APP_BASE_URL` | links em e-mails e OAuth |
| `SUPERADMIN_EMAIL`, `SUPERADMIN_PASSWORD` | provisiona o superadmin (12+ caracteres). Depois do primeiro acesso, ativar 2FA |
| `EMAIL_PROVIDER=resend`, `RESEND_API_KEY`, `EMAIL_FROM` | e-mail |
| `STORAGE_PROVIDER=s3`, `S3_ENDPOINT`, `S3_BUCKET`, `S3_ACCESS_KEY_ID`, `S3_SECRET_ACCESS_KEY`, `S3_REGION=auto` | arquivos (R2) |
| `PAYMENT_GATEWAY=asaas`, `ASAAS_API_KEY`, `ASAAS_WEBHOOK_TOKEN` | cobrança |
| `SENTRY_DSN` (opcional), `SENTRY_TRACES_SAMPLE_RATE` (padrão 0.05) | rastreamento de erros |
| `META_*`, `INSTAGRAM_*`, `TIKTOK_*`, `GOOGLE_*` | integrações de marketing |
| Ajustes: `RATE_LIMIT_LOGIN`, `RATE_LIMIT_CONTA`, `SECURITY_STAMP_INTERVAL_SECONDS`, `BILLING_TOLERANCIA_DIAS`, `STORAGE_MAX_MB`, `STORAGE_URL_TTL_SECONDS` | opcionais |
| Chaves de segurança (padrão ligado): `SUPERADMIN_EXIGIR_2FA`, `LGPD_EXIGIR_ACEITE` | só desligar em teste |

## 3. Autenticação em duas etapas
- Qualquer usuário ativa em *Segurança da conta → Autenticação em duas etapas* (QR code para Google/Microsoft Authenticator, Authy, 1Password).
- 10 códigos de recuperação de uso único; podem ser regenerados com um código do app.
- Desativar exige senha **e** código. Ativar/desativar gera e-mail de aviso e auditoria.
- **Superadmin só entra no painel global com 2FA ativo** (redirecionado para ativar).
- Tentativas erradas contam no bloqueio da conta (5 → 5 min) e no limite por IP.

## 4. Monitoramento e alertas

| Item | Onde | Alerta |
|---|---|---|
| Disponibilidade | `/health/live` (processo), `/health/ready` (banco; 503 se fora) | **(ação)** monitor de uptime (Better Stack ou UptimeRobot) a cada 1 min em `/health/ready`, alerta por e-mail/WhatsApp |
| Página pública de status | `/status` e `/status.json` (sem detalhes internos) | link no rodapé/site |
| Banco, armazenamento, rotinas, integrações | health checks; detalhes em `/Plataforma/Saude` | aparece como "Instabilidade" no status |
| Rotinas em segundo plano | batimentos de `sincronizacao-marketing`, `assinaturas`, `retencao` | degradado se a última execução falhou ou atrasou 2,5× o intervalo |
| Integrações de marketing | contas com erro/token expirado em `/Plataforma/Saude` | revisar semanalmente |
| Webhooks de pagamento com falha | `/Plataforma/Saude` | o Asaas também reenvia e avisa |
| Exceções | Sentry (`SENTRY_DSN`) | **(ação)** criar projeto no Sentry e regra de alerta por e-mail |
| Logs | JSON no console (Railway indexa), nível Warning para SQL | sem senhas, tokens, query strings de links, cookies |

Privacidade nos logs e no Sentry: `SendDefaultPii=false`, sem corpo de requisição, sem cookies, sem query string (tem tokens de convite/redefinição), sem cabeçalhos de autenticação; usuário só pelo id.

## 5. Backups e recuperação de desastre

- **Rotina:** `.github/workflows/backup.yml` todo dia às 03:00 (Brasília): `pg_dump -Fc` + manifesto de contagens + SHA-256 → bucket R2 **separado** (`agenciaos-backups`), retenção de 30 dias.
- **Teste de restauração a cada backup:** o mesmo workflow restaura num Postgres descartável e compara SHA-256, versão das migrations e contagens das tabelas principais com o manifesto, mais uma checagem de integridade entre agências. Se algo não bater, o workflow falha e o GitHub avisa por e-mail. Um backup que não restaura não conta como backup.
- Validado localmente em 31/10/2026: backup do banco local → restauração aprovada; com o manifesto adulterado de propósito, a restauração foi **reprovada** (código de saída 1).
- **(ação)** Criar o bucket `agenciaos-backups` (privado, token só para ele) e os segredos do repositório listados no topo do workflow. O Railway também oferece backups do volume do Postgres; manter os dois.

### Procedimento de restauração (produção)
1. Declarar incidente; colocar o app em manutenção (pausar o serviço no Railway).
2. Baixar o dump desejado: `aws s3 cp s3://agenciaos-backups/backups/producao/<arquivo>.dump . --endpoint-url $S3_ENDPOINT` (+ `.sha256` e `.manifest.txt`).
3. Restaurar primeiro num banco novo e rodar `scripts/backup/testar-restauracao.sh <arquivo>.dump` com `RESTORE_DATABASE_URL` apontando para ele.
4. Aprovado: apontar `ConnectionStrings__DefaultConnection` para o banco restaurado (ou restaurar no banco de produção com `pg_restore --clean`).
5. Subir o serviço, conferir `/health/ready`, login, uma agência de teste e `/Plataforma/Saude`.
6. Registrar no relatório do incidente o horário do backup usado (perda máxima de dados = tempo desde o último backup, **RPO 24 h**; tempo alvo de recuperação **RTO 2 h**).
7. Arquivos (R2) não dependem do banco; se o bucket for perdido, restaurar pelo versionamento do R2 (**(ação)** ativar versionamento/lifecycle no bucket de arquivos).

## 6. Retenção automática
`RetencaoWorker` (diário) apaga registros de login com mais de 6 meses, auditoria com mais de 5 anos, eventos de webhook processados e histórico de sincronização com mais de 1 ano, notificações lidas com mais de 1 ano. Nunca apaga dados de negócio das agências (testado). Exclusão de agências canceladas após o prazo continua manual (ação sensível, ver `docs/legal/POLITICA_RETENCAO.md`).

## 7. CI/CD
- `.github/workflows/ci.yml` em todo push e pull request: verifica dependências vulneráveis, compila e roda **todos** os testes com Postgres e servidor S3 reais em containers, depois compila a imagem Docker do deploy.
- **(ação)** No Railway, no serviço do app: *Settings → Deploy → Wait for CI* (ou "Check Suites"), para o deploy só acontecer quando o CI passar. Sem isso o Railway publica direto a cada push.
- **(ação)** No GitHub: proteger a branch `main` (pull request obrigatório + CI verde).
- Migrations rodam no startup do app; antes de mesclar algo com migration, conferir que o workflow de backup do dia passou.
