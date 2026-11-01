# Roadmap de implementação — AgenciaOS SaaS

Cada fase termina com entrega verificável e testes. Nada vai para produção sem aprovação explícita do proprietário.

| Fase | Entrega | Status |
|---|---|---|
| 0 | Auditoria, arquitetura e plano (`docs/architecture/`) | Concluída |
| 1 | Multi-tenancy e isolamento | Concluída localmente (não publicada) |
| 2 | Armazenamento seguro (R2/S3) | Concluída localmente (não publicada) |
| 3 | Cadastro público, confirmação de e-mail, recuperação de senha, convites | Concluída localmente (não publicada) |
| 4 | E-mail transacional, planos, assinaturas, gateway, bloqueio | Concluída localmente (gateway real ainda não configurado) |
| 5 | LGPD e painel administrativo global com auditoria | Concluída localmente (não publicada) |
| 6 | 2FA, backups testados, homologação, monitoramento, CI/CD | Concluída localmente (ações no Railway/GitHub pendentes) |
| 7 | Domínios personalizados e aprovação Meta/TikTok/Google | Concluída localmente (aprovações das plataformas não submetidas) |

## Fase 1 — Multi-tenancy e isolamento

1. Entidade `Agencia`, interface `IPertenceAgencia`, `AgenciaId` nas entidades (ver `MULTITENANCY_DESIGN.md`).
2. `ITenantContext` + `TenantMiddleware` + filtros globais + guarda no `SaveChanges`.
3. Migration com migração da agência atual (ver `DATABASE_MIGRATION.md`), ensaiada em cópia do banco local.
4. `AcessoService` e correção dos controllers (S01–S05, S07, S09, S10, S12).
5. Vínculo usuário Cliente ↔ Cliente na tela de clientes (hoje não existe, por isso o perfil Cliente não funcionava).
6. Role `SuperAdmin`, provisionamento por variável de ambiente e painel mínimo `/Plataforma` (listar agências e criar agência + primeiro admin) para operar várias agências antes da Fase 3.
7. Remoção da senha padrão do seed.
8. Testes:
   - Cenário 1: Alpha consulta clientes da Beta → negado.
   - Cenário 2: usuário da Alpha modifica tarefa da Beta → bloqueado.
   - Cenário 3: Cliente A vê dados do Cliente B na mesma agência → negado.
   - Cenário 4: AgenciaId forjado no formulário → ignorado/negado.
   - Cenário 6 (parcial): upload para cliente de outra agência → negado. Download privado depende da Fase 2.
   - Cenário 5 (inadimplência) → Fase 4.
   - Regressão: testes de marketing existentes continuam passando.

### Diário da Fase 1 (30/10/2026)

1. Backup do banco local (`backups/antes-multitenant-20261030.dump`, fora do git) e **restauração testada** em `agenciaos_restore_test`.
2. Na cópia restaurada: carga de dados de exemplo (tarefas, conteúdo com comentário, financeiro, contrato, arquivo, data comemorativa) → migration aplicada → `scripts/validar-migracao-tenant.sql` com 0 problemas → `Down()` executado (dados preservados, colunas removidas) → migration reaplicada.
3. Migration aplicada no banco local principal; validação com 0 problemas; login do admin existente, clientes, edição de cliente, configuração e marketing conferidos no navegador.
4. Testes: 51 passando (33 antigos + 8 de isolamento no DbContext + 10 HTTP de ponta a ponta contra Postgres). Prova de que os testes pegam vazamento: com o isolamento desligado de propósito, 20 testes falham.

**Como rodar os testes**

```bash
dotnet test tests/AgenciaOS.Tests -c Release
```

Os testes HTTP usam o Postgres local do Docker (porta 5434) e recriam o banco `agenciaos_tests`. Em outro ambiente, defina `AGENCIAOS_TEST_PG` (ex.: `Host=...;Port=...;Username=...;Password=...`, sem `Database`).

**Arquivos principais**

- `Models/Agencia.cs` (entidade, enums, `IPertenceAgencia`)
- `Data/ApplicationDbContext.Tenancy.cs` (filtros globais + guarda de escrita)
- `Services/Tenancy/TenantContext.cs` (contexto, middleware, `CriarEscopoSistema`, `DaAgencia`)
- `Services/Tenancy/PermissaoModuloFilter.cs` (superadmin × agência, abas no servidor, 403 para acesso entre agências)
- `Services/AcessoService.cs` (o que cada perfil vê dentro da agência)
- `Data/SeedInicial.cs` (sem senha padrão; superadmin por variável de ambiente)
- `Controllers/PlataformaController.cs` + `Views/Plataforma/*` (painel mínimo)
- `Migrations/20261030171004_MultiTenancyAgencias.cs`
- `tests/AgenciaOS.Tests/TestesMultiTenant.cs`, `TestesHttpMultiTenant.cs`

**Pendências para publicar a Fase 1 em produção (precisa de aprovação)**

1. Commit (o módulo de marketing e a Fase 1 ainda não estão no git).
2. Backup da produção + restauração de teste.
3. Definir no Railway `SUPERADMIN_EMAIL` e `SUPERADMIN_PASSWORD` (12+ caracteres).
4. Deploy (a migration roda no startup) + script de validação.
5. Trocar a senha do admin de produção se ainda for a padrão antiga.

## Fase 2 — Armazenamento seguro

Desenho em `MULTITENANCY_DESIGN.md` §4.6 e decisões D13, D15, D16.

### Diário da Fase 2 (30/10/2026)

1. `Services/Armazenamento/`: interface, provedores local e S3, validador, serviço de upload, migração. `MarcaController` para logos. Migration aditiva `ArmazenamentoPrivadoArquivos` (colunas `ChaveArmazenamento`, `Provedor`, `HashSha256` em `Arquivos`).
2. Backup `backups/antes-fase2-20261030.dump`, migration aplicada no banco local, `--migrar-arquivos` executado (banco local não tinha arquivos nem logo para migrar).
3. Teste de ponta a ponta no app local: upload de PDF, download autenticado com conteúdo idêntico, anônimo redirecionado para login. Repetido com `STORAGE_PROVIDER=s3` contra um servidor S3 local (container `adobe/s3mock`): o download redireciona para URL assinada e o conteúdo bate.
4. Testes: 71 passando. Novos: validação (executável disfarçado, conteúdo trocado, tamanho real, SVG nunca inline, nomes e chaves perigosos), provedor local, provedor S3 contra servidor S3 real, migração com hash/idempotência, Cenário 6 completo por HTTP. Com a verificação de dono removida de propósito do download, o Cenário 6 falha.

### Como ativar o R2 em produção (precisa de aprovação)

1. Cloudflare → R2 → criar bucket privado (ex.: `agenciaos-prod`) e um token de API com permissão de leitura/escrita só nesse bucket.
2. No Railway, variáveis:
   - `STORAGE_PROVIDER=s3`
   - `S3_ENDPOINT=https://<account-id>.r2.cloudflarestorage.com`
   - `S3_REGION=auto`
   - `S3_BUCKET=agenciaos-prod`
   - `S3_ACCESS_KEY_ID=...` / `S3_SECRET_ACCESS_KEY=...`
   - opcionais: `STORAGE_URL_TTL_SECONDS` (padrão 300), `STORAGE_MAX_MB` (padrão 50)
3. Deploy. Depois, uma vez: `dotnet AgenciaOS.dll --migrar-arquivos` (sem `--remover-originais`), conferir o relatório no log, testar downloads, e só então rodar com `--remover-originais`.
4. Para homologação, usar outro bucket e outro token.

**Observação:** hoje, em produção, `wwwroot/uploads` está no disco efêmero do container; arquivos enviados antes do último deploy podem já ter se perdido. A migração informa quais registros não têm mais o arquivo original.

### Testes de armazenamento S3

Usam `http://127.0.0.1:9090` (container local `agenciaos-s3mock`, bucket `agenciaos-test`). Para rodar contra um bucket R2 de homologação: `AGENCIAOS_TEST_S3_ENDPOINT`, `AGENCIAOS_TEST_S3_BUCKET`, `AGENCIAOS_TEST_S3_KEY`, `AGENCIAOS_TEST_S3_SECRET`.

## Fase 3 — Cadastro e autenticação

### O que foi entregue (30/10/2026)

| Item | Onde |
|---|---|
| Cadastro público `/Account/Cadastro`: agência (status Ativa, assinatura em Teste por 14 dias) + configuração + primeiro Admin + aceite de Termos e Privacidade com versão, data, IP e navegador | `Services/Contas/ContasService.cs`, `Models/Contas.cs` (`ConsentimentoTermos`, `DocumentosLegais`) |
| Confirmação de e-mail obrigatória para novas contas (`RequireConfirmedEmail`); reenvio sem revelar se a conta existe | `AccountController.ConfirmarEmail/ReenviarConfirmacao` |
| Recuperação de senha com link de 3 h, de uso único; a troca derruba todas as sessões | `AccountController.EsqueciSenha/RedefinirSenha` |
| Convites por e-mail (7 dias, token aleatório de 256 bits, só o hash SHA-256 no banco, revogáveis, novo convite invalida o anterior); o convidado define a própria senha e aceita os termos | `ConviteUsuario`, `UsuariosController.Convidar/RevogarConvite`, `AccountController.Convite` |
| Página "Segurança da conta": troca de senha (com aviso por e-mail) e "encerrar outras sessões" | `ContaController` |
| Política de senha: 10+ caracteres, maiúscula, minúscula e número (só para senhas novas). E-mail único. Mensagens do Identity em português | `Program.cs`, `IdentityErrosPortugues` |
| Anti-abuso: rate limit por IP (login 20/5 min; cadastro, reenvio, recuperação e convites 5/15 min), honeypot no cadastro, cadastro com e-mail já usado responde igual e avisa o dono do e-mail | `RateLimitingContas`, `AccountController.Cadastro` |
| Revogação de sessões: security stamp revalidado a cada 5 min (`SECURITY_STAMP_INTERVAL_SECONDS`) | `Program.cs` |
| E-mail transacional mínimo (`IEnviadorEmail`): Resend em produção, log em desenvolvimento, memória nos testes; template com a identidade do AgenciaOS | `Services/Email/Email.cs` (completado na Fase 4) |
| Superadmin continua só por `SUPERADMIN_EMAIL/SUPERADMIN_PASSWORD`; o cadastro sempre cria Admin de uma agência nova, ignorando campos extras | testado |
| Login bloqueado para usuário desativado ou agência suspensa/cancelada | `AccountController.Login` |
| Minutas de Termos de Uso e Política de Privacidade (marcadas para revisão jurídica) | `Views/Account/Termos.cshtml`, `Privacidade.cshtml` |

Migration aditiva `ContasConvitesConsentimentos` aplicada no banco local (backup `backups/antes-fase3-20261030.dump`).

Testes: 80 passando (9 novos por HTTP: cadastro + confirmação + isolamento da nova agência, tentativa de virar superadmin, e-mail duplicado, honeypot/termos/senha fraca, recuperação com token adulterado/reuso/queda de sessão, convite completo e entre agências, convite expirado/revogado, encerrar sessões, rate limit 429). Com a exigência de confirmação de e-mail desligada de propósito, o teste de cadastro falha.

### Variáveis de ambiente para produção (precisa de aprovação para configurar)

- `EMAIL_PROVIDER=resend`, `RESEND_API_KEY=...`, `EMAIL_FROM="AgenciaOS <nao-responda@seudominio>"` (domínio verificado no Resend)
- `APP_BASE_URL=https://<domínio do app>` (obrigatório: os links de e-mail nunca usam o cabeçalho Host em produção)
- opcionais: `RATE_LIMIT_LOGIN`, `RATE_LIMIT_CONTA`, `SECURITY_STAMP_INTERVAL_SECONDS`

Sem essas variáveis o app registra erro no log e cadastro/convites/recuperação não enviam e-mail.

### Pendências conhecidas
- Agência suspensa: o login é bloqueado, mas sessões já abertas continuam até a Fase 4 (middleware de bloqueio por assinatura).
- 2FA (TOTP): Fase 6.
- E-mail é único na plataforma: uma pessoa não pode ser usuária de duas agências com o mesmo e-mail.

## Fase 4 — E-mail e cobrança

### O que foi entregue (30/10/2026)

- **Modelo:** `Plano` (preço, periodicidade, limites de usuários/clientes/armazenamento, módulos, recursos, à venda), `Assinatura` (por agência, espelhada em `Agencia.SituacaoAssinatura`), `Pagamento` (histórico), `EventoWebhook` (idempotência). Migration aditiva `PlanosAssinaturasCobranca`.
- **Gateway:** `IGatewayPagamento` com `GatewayAsaas` (API v3: clientes, assinaturas, cobranças da assinatura, troca de valor, cancelamento) e `GatewaySimulado` (só fora de produção).
- **Checkout:** `/Assinatura/Planos` → escolhe plano, forma (PIX, cartão recorrente, boleto ou escolher na hora) e CPF/CNPJ → redireciona para a fatura hospedada do Asaas. Dados de cartão nunca passam pelo AgenciaOS.
- **Webhook:** `POST /webhooks/asaas`, token em `asaas-access-token` comparado em tempo constante, payload até 256 KB, idempotente, erro → 500 para o Asaas reenviar.
- **Ciclo:** Teste → (assina) PagamentoPendente → (webhook) Ativa → (vencida) Tolerância de N dias → Suspensa → (paga) Ativa. Cancelamento mantém o acesso até o fim do período pago e depois vira Cancelada. Troca de plano altera o valor das próximas cobranças. Rotina horária (`AssinaturasWorker`) aplica teste vencido, fim da tolerância e fim do período cancelado.
- **Bloqueio por agência (Cenário 5):** Suspensa, Cancelada, teste vencido ou pagamento pendente sem teste bloqueiam os módulos (tela de assinatura para o admin, aviso para os demais). Nenhum dado é apagado; outras agências não são afetadas. Na tolerância e no teste aparece um aviso no topo.
- **Limites e módulos do plano** aplicados no servidor: clientes ativos, usuários da equipe (convites pendentes contam), armazenamento de arquivos, módulos (redireciona para "módulo indisponível").
- **Painel global:** `/Plataforma/Planos` para cadastrar/editar planos sem alterar código.
- **E-mails:** pagamento confirmado, atraso, suspensão, fim do teste, cancelamento (via `IEnviadorEmail`).
- **Testes:** 104 passando. Novos: cliente Asaas (cabeçalhos e campos da API, erro legível, leitura do webhook), regras de bloqueio, ciclo completo da assinatura (teste→pagamento, idempotência, atraso→tolerância→suspensão→reativação com dados preservados, evento fora de ordem, assinatura desconhecida, teste vencido, cancelamento), HTTP (token do webhook, Cenário 5, teste vencido, checkout só ativa por webhook, webhook isolado por agência, limites e módulos, planos pelo superadmin). Com o bloqueio e a idempotência desligados de propósito, 4 testes falham.

### Para ativar em produção (precisa de aprovação)

1. Criar conta no Asaas, gerar a chave de API (sandbox primeiro: começa com `$aact_hmlg_`).
2. Railway: `PAYMENT_GATEWAY=asaas`, `ASAAS_API_KEY=...`, `ASAAS_WEBHOOK_TOKEN=<segredo longo aleatório>`, opcional `BILLING_TOLERANCIA_DIAS` (padrão 7).
3. No painel do Asaas, cadastrar o webhook `https://<APP_BASE_URL>/webhooks/asaas` com o mesmo token, eventos de cobrança, envio sequencial.
4. Cadastrar os planos em `/Plataforma/Planos`.
5. Validar no sandbox: assinar, pagar a fatura de teste, conferir a ativação; depois trocar para a chave de produção.

### Pendências conhecidas
- Taxas do gateway a confirmar nas páginas oficiais (D14).
- A agência fundadora (Id 1) segue como "Ativa" sem assinatura: decidir se ela passa a pagar ou fica isenta.
- Painel global sem MRR/ARR/churn ainda (Fase 5).
- Pix Automático (autorização recorrente) não foi usado: as assinaturas PIX geram uma cobrança por ciclo que o cliente paga pela fatura.

## Fase 5 — LGPD e administração global

### O que foi entregue (30/10/2026)

- **Isenção:** `Agencia.Isenta` (sem cobrança, sem bloqueio comercial, sem limites de plano). Decisão do proprietário: a agência fundadora (Id 1, Social Unânime) é isenta (aplicado pela migration `LgpdAuditoriaPlataforma`). Pode ser alterado em `/Plataforma/Agencia/{id}`.
- **Painel global** (`/Plataforma`, só SuperAdmin, navegação própria): MRR, ARR (anual normalizado ÷ 12), taxa de cancelamento de 30 dias, novas agências, ativas/teste/inadimplentes/suspensas/canceladas/isentas, usuários. Detalhes da agência com usuários, assinatura, pagamentos e atividades; suspender (com motivo, derruba sessões) e reativar; isentar; alterar plano; estender teste.
- **Configurações globais** (`/Plataforma/Configuracoes`): dias de teste, dias de tolerância, encarregado de dados (aparece na Política), e-mail de suporte, exigir novo aceite.
- **Auditoria** (`RegistroAuditoria`, `/Plataforma/Auditoria` com filtros): logins (sucesso e falha), criação/ativação/permissões de usuários, convites, assinatura (assinar, trocar, cancelar), todas as ações do painel global, exportações e ações de privacidade. Guarda quem, quando, IP, agência e alvo; nunca senhas ou tokens.
- **LGPD:**
  - Novo aceite obrigatório quando a versão dos Termos/Política muda (`AceiteTermosFilter`; histórico em "Minha privacidade").
  - "Minha privacidade" para todo usuário: baixar os próprios dados (JSON, sem segredos), fazer pedidos, ver aceites.
  - Formulário público `/Privacidade/Solicitar` para qualquer titular (com protocolo, prazo de 15 dias, e-mail de confirmação, anti-robô e limite por IP).
  - Gestão dos pedidos no painel global (responder, recusar, encaminhar à agência controladora, exportar dados do titular, anonimizar titular) e na agência (`/Privacidade`, só os pedidos encaminhados a ela).
  - Área "Dados e LGPD" da agência: exportação completa em ZIP (JSON por módulo, só os dados da agência, sem senhas/tokens), exclusão de dados pessoais de um cliente (mantém financeiro/contratos/tarefas sem identificação, apaga conexões de marketing) e anonimização de usuário.
  - Minutas para revisão jurídica: `docs/legal/ACORDO_TRATAMENTO_DADOS.md` (controladora × operador, suboperadores, incidentes) e `docs/legal/POLITICA_RETENCAO.md`.
- **Correção encontrada pelos testes:** a versão texto dos e-mails saía com acentos como entidades HTML (`exclu&#xED;dos`); agora é decodificada.
- **Testes:** 115 passando. Novos: indicadores (MRR/ARR/churn), isenta nunca bloqueada, suspensão/reativação com queda de sessão e auditoria, painel inacessível para agências, exportação só com dados da própria agência e sem segredos, exportação do titular, pedido público encaminhado só à agência certa, anonimização preservando aceites, exclusão de cliente mantendo financeiro e sem afetar outra agência, novo aceite obrigatório, dias de teste configuráveis.

### Observações
- No primeiro acesso depois da migration, todos os usuários existentes passam pela tela de aceite (ainda não havia registro de aceite).
- Retenção automática (apagar dados de canceladas após o prazo, limpar logs antigos) fica para a Fase 6.

## Fase 6 — Operação

Guia completo: `docs/operations/OPERACAO.md` (ambientes, variáveis, monitoramento, backups, recuperação de desastre, CI/CD).

### O que foi entregue (31/10/2026)
- **2FA (TOTP)** com QR code, 10 códigos de recuperação, segunda etapa no login, desativação com senha + código, avisos por e-mail e auditoria. **Obrigatório para o superadmin** entrar no painel global.
- **Monitoramento:** health checks (banco, armazenamento, rotinas em segundo plano, configuração de e-mail/pagamentos), `/health/live` e `/health/ready`, tela `/Plataforma/Saude` (detalhes + contas de marketing com erro, webhooks com falha, pedidos LGPD atrasados), logs JSON fora do desenvolvimento, Sentry opcional sem dados pessoais.
- **Página pública de status:** `/status` e `/status.json`, sem detalhes internos.
- **Retenção automática** diária (logins 6 meses, webhooks/sincronizações/notificações lidas 1 ano, auditoria 5 anos).
- **Backups:** `scripts/backup/backup-postgres.sh` + `scripts/backup/testar-restauracao.sh` + workflow diário `.github/workflows/backup.yml` (R2, 30 dias, restauração conferida por SHA-256, versão de migration, contagens e integridade entre agências). Validado localmente: aprovado com backup real e reprovado com manifesto adulterado.
- **Homologação:** `Staging` segue as regras de produção; `scripts/homologacao/anonimizar-copia.sql` (testado numa cópia local) + `--definir-senha` para testes (recusado em produção).
- **CI:** `.github/workflows/ci.yml` (dependências vulneráveis, testes com Postgres e S3 reais, imagem Docker). Dependência vulnerável `System.Security.Cryptography.Pkcs` corrigida (10.0.14).
- **Bugs encontrados pelos testes e corrigidos:** abrir a tela de 2FA derrubava a sessão do próprio usuário (troca de security stamp); códigos de recuperação eram recusados porque o hífen era removido.
- **Testes:** 120 passando (2FA ponta a ponta, 2FA obrigatório do superadmin, health/status sem vazamento de detalhes, retenção, gateway simulado proibido em homologação/produção).

### Ações do proprietário (fora do código)
Monitor de uptime, projeto no Sentry, bucket e segredos de backup, ambiente `staging` no Railway, "Wait for CI" no Railway, proteção da branch `main`. Detalhes em `docs/operations/OPERACAO.md`.

## Fase 7 — Domínios e integrações

### O que foi entregue (01/11/2026)
- **Domínio próprio da agência** (`/Dominios`, só Admin): cadastro com normalização (esquema, porta, maiúsculas, acentos → punycode), verificação de posse por TXT `_agenciaos.<host>`, instruções de CNAME, domínio principal, remoção. Subdomínio automático `{slug}.<PLATAFORMA_DOMINIO_BASE>` já verificado. Painel global `/Plataforma/Dominios` para configurar HTTPS no Railway.
- **Resolução segura** (`DominioMiddleware`): o domínio só define a marca do login; usuário de outra agência é recusado no login e redirecionado se já estiver logado; com `PLATAFORMA_HOSTS`, hosts desconhecidos recebem 404 (exceto `/health`, webhooks e callbacks). Ver D30 e D31.
- **Integrações revisadas:** escopos mínimos de leitura, OAuth com state + PKCE, tokens criptografados, revogação ao desconectar e tratamento de token revogado já estavam corretos (Central de Marketing).
- **Exclusão de dados e desautorização:** callbacks da Meta (exclusão de dados com código de confirmação e página pública de situação; desautorização), webhook `authorization.removed` do TikTok, botão "Excluir dados" por integração (todas as plataformas, inclusive Google). Todos com verificação de assinatura.
- **Política de Privacidade e Termos** com o uso dos dados de Meta, TikTok e Google, declaração de Uso Limitado do Google e como excluir.
- **Dossiê de aprovação:** `docs/integracoes/APROVACAO_PLATAFORMAS.md` (URLs, escopos e justificativas, roteiro de vídeo, checklist). **Nenhuma plataforma foi submetida nem aprovada.**
- **Testes:** 135 passando (normalização de domínios, verificação por DNS simulado, marca e bloqueio de uso cruzado no login, 404 de host desconhecido, assinatura Meta válida/inválida/algoritmo errado, isolamento da exclusão por usuário, assinatura e janela de tempo do TikTok, "Excluir dados" só na própria agência). Os testes acharam e corrigimos: código de confirmação da Meta maior que a coluna; login de domínio sem marca quando a agência não tinha identidade visual salva.

### Para ativar (ações do proprietário)
1. Variáveis: `PLATAFORMA_HOSTS=app.seudominio.com.br`, `PLATAFORMA_CNAME_ALVO=<domínio do serviço no Railway>`, opcional `PLATAFORMA_DOMINIO_BASE=agenciaos.com.br` (com DNS curinga `*.agenciaos.com.br` → Railway e domínio curinga no serviço).
2. Para cada domínio verificado em `/Plataforma/Dominios`: Railway → serviço → Settings → Networking → Custom Domain.
3. Seguir `docs/integracoes/APROVACAO_PLATAFORMAS.md` para criar os apps e submeter as revisões.
