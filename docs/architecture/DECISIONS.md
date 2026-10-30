# Decisões técnicas (ADR resumidas)

## D01 — Multi-tenancy com banco único e coluna `AgenciaId`
**Contexto:** dezenas/centenas de agências pequenas, um único deploy no Railway, EF Core já em uso.
**Opções:** (a) banco por agência; (b) schema por agência; (c) banco único com coluna discriminadora.
**Decisão:** (c).
**Por quê:** custo e operação (uma migration, um backup, um pool de conexões), novas agências sem provisionar infraestrutura, suporte nativo do EF Core a filtros globais. (a)/(b) só compensam com exigência contratual de isolamento físico, que não existe hoje.
**Consequência:** o isolamento depende de código; por isso ele foi concentrado em três pontos testados (middleware, filtros globais, guarda no SaveChanges).

## D02 — Nomes em português (`Agencia`, `AgenciaId`)
O código inteiro usa português (`Cliente`, `Tarefa`, `Financeiro`). Manter o padrão evita mistura.

## D03 — Tenant resolvido pelo usuário autenticado, consultando o banco
**Por quê:** é a única fonte confiável. Claim no cookie seria mais rápido, mas ficaria desatualizado se o usuário mudasse de agência e quebraria cookies emitidos antes do deploy. Custo: uma consulta por PK por requisição (o layout já consulta o usuário em toda tela).

## D04 — Identidade visual continua em `Configuracao` (uma por agência)
Evita duplicar logo/cores em `Agencia` e não muda as telas existentes. `Agencia` guarda dados cadastrais e comerciais.

## D05 — `Usuario` sem filtro global
O Identity busca usuários antes de existir tenant (login, lockout, reset de senha). Um filtro global quebraria esses fluxos. Compensação: listagens filtram explicitamente e a guarda de escrita valida usuários referenciados e alterados.

## D06 — Consistência entre agências na aplicação, não em FKs compostas
FKs compostas `(AgenciaId, ClienteId)` exigem chaves alternativas em todas as tabelas-pai e relacionamentos sobrepostos no EF (coluna `AgenciaId` compartilhada por várias FKs), o que complica `Include`, cascatas e campos anuláveis (`Tarefa.ClienteId`). A guarda no `SaveChanges` cobre todas as FKs automaticamente (lê o modelo do EF) e é testada. Reavaliar na Fase 6 se houver escrita fora do EF (scripts SQL, ETL).

## D07 — Falha fechada sem tenant
Sem tenant resolvido, consultas a dados de agência retornam vazio e escritas lançam exceção. Prefiro uma tela vazia a um vazamento.

## D08 — Tela de login com identidade do AgenciaOS
Como o login é compartilhado, a tela de login (anônima) usa a identidade padrão do AgenciaOS, não a da agência. Na Fase 7, o domínio/subdomínio da agência poderá definir a marca exibida no login.

## D09 — Superadministrador fora das agências
`AgenciaId = NULL` + role `SuperAdmin`. Criado apenas no startup a partir de `SUPERADMIN_EMAIL` e `SUPERADMIN_PASSWORD` (variáveis de ambiente). Não existe tela ou endpoint que promova alguém a superadmin. O superadmin não navega nos módulos das agências; o painel global (`/Plataforma`) é separado e terá auditoria na Fase 5.

## D10 — Sem senha padrão no seed
Se não houver `ADMIN_PASSWORD`, o seed não cria o admin em produção; em desenvolvimento gera uma senha aleatória e escreve no log uma única vez.

## D11 — Permissão por módulo validada no servidor
`AbasPermitidas` passa a ser aplicada por um filtro MVC global, além do menu. O Dashboard fica sempre liberado porque é a página inicial (sem ele, um usuário restrito entraria em loop de acesso negado); o conteúdo do Dashboard já é limitado pelo perfil.

## D12 — Testes de isolamento em dois níveis
- Unidade/serviço com SQLite em memória (rápidos, sem infraestrutura).
- HTTP de ponta a ponta com `WebApplicationFactory` contra um Postgres de teste (`agenciaos_tests`), aplicando as migrations reais e fazendo login real. São esses que provam que um ID forjado na URL ou no formulário não atravessa agências.

## D13 — Armazenamento de arquivos: Cloudflare R2 via API S3
**Decisão (Fase 2):** R2 como provedor de produção recomendado, acessado pelo `AWSSDK.S3` atrás de `IArmazenamentoArquivos`.
**Por quê:** sem taxa de saída (agências baixam muita mídia), API S3 compatível (trocar por Amazon S3 é só mudar `S3_ENDPOINT`/região), URLs assinadas nativas, preço de armazenamento baixo. Amazon S3 fica como alternativa se houver exigência de região/compliance AWS.
**Detalhes:** checksums automáticos do SDK v4 desligados (`WHEN_REQUIRED`) e upload sem chunked encoding, porque R2 e outros compatíveis não suportam; bucket sempre privado; download via URL assinada de 5 min gerada só depois da verificação de dono.
**Antivírus:** não há varredura de malware (ClamAV ou serviço externo) nesta fase. Mitigação: lista branca, assinatura binária, bloqueio de executáveis, nunca servir HTML/SVG inline, `nosniff` + CSP sandbox. Reavaliar na Fase 6.

## D15 — Armazenamento local fora de `wwwroot`
Para desenvolvimento e para quem usar volume no Railway: `App_Data/storage` (ou `STORAGE_LOCAL_PATH`). Nada ali é servido como estático; o acesso passa pelo mesmo endpoint autenticado. Em produção sem S3 o app registra aviso no log, porque o disco do container se perde a cada deploy.

## D17 — E-mail mínimo já na Fase 3, com Resend
Cadastro, convite e recuperação não funcionam sem e-mail, então a interface `IEnviadorEmail` entrou na Fase 3 com Resend (API HTTP simples, domínio verificado, plano gratuito suficiente para começar). Templates em C# (HTML inline e versão texto) com a identidade do AgenciaOS. A Fase 4 completa os avisos de assinatura/cobrança. Amazon SES fica como alternativa de custo em volume.

## D18 — Respostas que não revelam contas
Cadastro com e-mail existente, reenvio de confirmação e "esqueci a senha" respondem igual exista ou não a conta. No cadastro duplicado, o dono do e-mail recebe um aviso.

## D19 — Confirmação de e-mail só barra quem ainda não confirmou
`RequireConfirmedEmail` foi ligado sem migrar dados: usuários existentes e convidados já têm `EmailConfirmed = true` (o convite chegou no e-mail). Só contas do cadastro público dependem do link.

## D20 — Convites com token próprio em vez do token do Identity
O convidado ainda não tem usuário, então o token do Identity não serve. Token aleatório de 256 bits, guardado só como SHA-256, com expiração, revogação e uso único; o usuário só é criado ao aceitar.

## D21 — Rate limiting por IP com limites configuráveis
Login 20 por 5 min e ações que enviam e-mail ou criam contas 5 por 15 min, por IP, além do bloqueio por conta do Identity. Limites lidos a cada requisição (`RATE_LIMIT_LOGIN`, `RATE_LIMIT_CONTA`), o que permite ajustar sem novo deploy de código.

## D16 — Escrita direta no destino (armazenamento local)
O provedor local grava direto no caminho final (chaves com GUID nunca colidem) em vez de gravar `.tmp` e renomear: no Windows o antivírus segura o arquivo recém-criado e o `Move` falha com "Access denied" (visto no teste local). Em caso de erro, o arquivo parcial é apagado.

## D14 — E-mail e cobrança (Fase 4, proposta)
- E-mail: Resend (API simples, bom free tier, domínio verificado) atrás de `IEnviadorEmail`; SES como alternativa de custo em volume.
- Cobrança: Asaas como primeira opção para o mercado brasileiro (PIX, boleto e cartão recorrente nativos, webhooks, sem exigir empresa no exterior). Stripe como alternativa se houver venda internacional. Decisão final após comparar taxas atuais na Fase 4.
