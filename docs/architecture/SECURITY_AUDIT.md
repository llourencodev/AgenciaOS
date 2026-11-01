# Auditoria de segurança

> Fase 0, 30/10/2026. Severidade: **Crítica** (vazamento/alteração de dados de terceiros), **Alta**, **Média**, **Baixa**.
> Coluna "Status" é atualizada a cada fase.

| # | Severidade | Onde | Problema | Status |
|---|---|---|---|---|
| S01 | Crítica | `ClientesController.Index/Details` | Qualquer usuário autenticado (inclusive Cliente e ColaboradorExterno) lista todos os clientes e abre `Clientes/Details/{id}` de qualquer cliente, com estratégia, objetivos, tarefas e arquivos | Corrigido na Fase 1 |
| S02 | Crítica | `CalendarioController.Index`, `ArquivosController.Index` | Para usuário Cliente sem vínculo (`UsuarioClienteId` nunca é gravado), o filtro não é aplicado e ele vê **todos** os conteúdos e arquivos | Corrigido na Fase 1 (falha fechada) |
| S03 | Crítica | `CalendarioController.Details/Eventos/Comentar` | `Details(id)` e `Eventos?clienteId=` não verificam dono; `Comentar` permite comentar conteúdo de outro cliente | Corrigido na Fase 1 |
| S04 | Crítica | `ClientesController.Edit` e `TarefasController.Edit` (POST) | Fazem `db.Update(model)` com a entidade inteira vinda do formulário (over-posting). Um ID forjado sobrescreve qualquer registro, e campos ocultos (`UsuarioClienteId`, `CriadoPorId`) podem ser trocados | Corrigido na Fase 1 (carrega do banco e copia campos permitidos + guarda no `SaveChanges`) |
| S05 | Crítica | `TarefasController.Index/AlterarStatus` | Cliente vê todas as tarefas da agência e pode mudar o status de qualquer tarefa | Corrigido na Fase 1 |
| S06 | Crítica | Arquivos em `wwwroot/uploads` | Servidos como estáticos sem autenticação: quem tem a URL baixa o arquivo | Corrigido na Fase 2 (armazenamento privado, `/uploads` bloqueado, download autenticado) |
| S07 | Alta | `ArquivosController.Upload` | Sem antiforgery e sem checar se o `clienteId` pertence ao usuário (Cliente envia arquivo para outro cliente); MIME vindo do navegador | Corrigido nas Fases 1 e 2 (dono validado; assinatura binária, bloqueio de executáveis, MIME pelo servidor) |
| S08 | Alta | `Program.cs` seed | Senha padrão `Admin@2025!` quando `ADMIN_PASSWORD` não está definida | Corrigido na Fase 1 (sem senha padrão). **Ação manual: trocar a senha do admin em produção se ela nunca foi alterada** |
| S09 | Alta | `AbasPermitidas` | Permissão por módulo só esconde o menu; acessar a URL direto funciona | Corrigido na Fase 1 (filtro global no servidor) |
| S10 | Alta | `ConfiguracaoController.Salvar` | Upload de logo sem validação de extensão/tipo (SVG/HTML servido do mesmo domínio → XSS armazenado) e nome fixo `logo.ext` (em multi-tenant, uma agência sobrescreveria a outra) | Corrigido na Fase 1 (só PNG/JPG/WEBP, até 2 MB, pasta por agência) |
| S11 | Alta | Todos os controllers | Nenhuma consulta tem dono além de cliente: com várias agências, tudo vazaria | Corrigido na Fase 1 (filtros globais + guarda de escrita) |
| S12 | Média | `UsuariosController` | Lista/ativa/edita qualquer usuário; em multi-tenant afetaria outras agências | Corrigido na Fase 1 |
| S13 | Média | `ContratosController.Excluir`, `FinanceiroController.MarcarPago/Excluir`, `ArquivosController.Excluir`, `ClientesController.Ativar` | POST sem `[ValidateAntiForgeryToken]` em parte deles (Ativar, Excluir de arquivos) | Corrigido na Fase 1 onde a view já envia o token; revisar na Fase 6 |
| S14 | Média | `ConfiguracaoController.Tema` | Anônimo, devolve a entidade inteira | Mantido (só identidade visual). Em multi-tenant devolve a configuração padrão para anônimos |
| S15 | Média | Login | Sem rate limit por IP (só lockout por conta), sem 2FA, sem confirmação de e-mail | Corrigido nas Fases 3 e 6 (rate limit, confirmação de e-mail, 2FA TOTP; obrigatório para superadmin) |
| S16 | Média | Senhas | Política fraca (8 caracteres, sem complexidade) | Corrigido na Fase 3 (10+, maiúscula, minúscula, número) |
| S17 | Média | `DashboardController` | Equipe vê receita/despesa do mês | Mantido (regra de negócio atual). Pode virar permissão na Fase 3 |
| S18 | Baixa | `Program.cs` | Ramo `UseSqlServer` legado | Mantido; remover quando confirmado que não é usado |
| S19 | Baixa | Deploy | Migrations no startup sem backup automático | Corrigido na Fase 6 (backup diário com restauração testada; ver docs/operations/OPERACAO.md) |
| S20 | Baixa | `AllowedHosts: *` | Necessário hoje; com domínios personalizados, validar host na Fase 7 | Corrigido na Fase 7: com `PLATAFORMA_HOSTS` definido, só hosts da plataforma e domínios verificados por DNS são atendidos (404 para os demais) |
| S21 | Alta | Dependências | `System.Security.Cryptography.Pkcs` 9.0.4 (transitiva) com vulnerabilidades conhecidas (GHSA-846f-f764-76mx, GHSA-2fmv-rv2c-59x7), apontada pelo `dotnet restore` | Corrigido na Fase 6 (referência direta à 10.0.14, corrigida; o CI agora falha se houver pacote vulnerável) |
| S22 | Média | `ConfiguracaoController.Tema` (anônimo) | Antes criava/gravava configuração; em multi-tenant isso falharia | Corrigido na Fase 1 (só leitura, campos de identidade visual) |
| S23 | Média | `UsuariosController.Ativar` | Desativar usuário não encerrava sessões abertas; admin podia se desativar | Corrigido na Fase 1 (security stamp atualizado; auto-desativação bloqueada) |
| S25 | Alta | Repositório git | `wwwroot/uploads/1/3e56bfd5-...pdf` (arquivo de cliente) está versionado no git. Não vai para a imagem Docker (`.dockerignore`), mas fica no histórico do repositório | **Pendente de decisão do proprietário**: remover do índice (`git rm --cached`) e avaliar limpeza do histórico. Não foi apagado |
| S26 | Média | Upload | Limite efetivo era o do Kestrel (~30 MB), não os 50 MB anunciados; arquivos maiores davam erro genérico | Corrigido na Fase 2 (limite próprio, configurável em `STORAGE_MAX_MB`) |
| S27 | Média | Arquivos | Sem varredura antivírus | Mitigado (ver D13); antivírus segue como melhoria futura |
| S28 | Alta | `UseForwardedHeaders` | Sem limpar redes conhecidas, atrás do proxy do Railway o IP do cliente seria o do proxy (rate limit por IP viraria global; logs sem IP real) | Corrigido na Fase 3 (em produção confia no proxy, `ForwardLimit = 1`) |
| S29 | Média | Links de e-mail | Risco de envenenamento do link de redefinição pelo cabeçalho Host | Corrigido na Fase 3 (`APP_BASE_URL` obrigatório em produção) |
| S30 | Baixa | Convites | Admin que convida um e-mail já cadastrado em outra agência recebe "já tem acesso" (revela que o e-mail existe na plataforma) | Aceito (e-mail único global); reavaliar se e-mail passar a ser por agência |
| S31 | Alta | Cobrança (Fase 4) | Riscos típicos: ativar plano pelo retorno do navegador, webhook forjado, reprocessamento duplicado, dados de cartão no servidor | Tratados: ativação só por webhook autenticado (token em tempo constante), idempotência por id do evento, checkout hospedado pelo gateway, gateway simulado bloqueado em produção |
| S32 | Baixa | Projeto de testes | `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 (usado só pelos testes com SQLite em memória) tem alerta sem versão corrigida publicada | Aceito: não vai para a imagem de produção nem processa dados externos. Reavaliar quando houver correção. `System.Security.Cryptography.Xml` dos testes atualizado para 10.0.14 |
| S33 | Alta | Integrações (Fase 7) | Exclusão de dados exigida por Meta/TikTok/Google não existia; callbacks públicos poderiam ser forjados | Implementado com verificação de assinatura (HMAC-SHA256 do signed_request da Meta; TikTok-Signature com janela de 5 min) e testes com assinatura inválida |
| S34 | Média | Domínios (Fase 7) | Domínio personalizado mal implementado permitiria escolher a agência pelo navegador | O domínio só define a marca do login; a agência dos dados vem sempre do usuário; posse provada por TXT; usuário de outra agência é recusado no login e redirecionado se já logado; host único por agência |
| S24 | Média | Dashboard | Colaborador externo via receita/despesa e tarefas de toda a agência | Corrigido na Fase 1 |

## Pontos positivos encontrados

- Tokens OAuth criptografados (Data Protection) e nunca enviados ao front-end.
- `state` + PKCE no OAuth; logs do HttpClient desligados para não vazar tokens.
- Antiforgery presente na maioria dos formulários.
- Lockout de login habilitado.
- Notificações já filtradas pelo dono.

## Credenciais e segredos

- Nenhuma chave de API encontrada no repositório. `appsettings.Development.json` está no `.gitignore`.
- A connection string local fica nesse arquivo; em produção vem de variável de ambiente.

## Testes de segurança definidos

Ver `IMPLEMENTATION_ROADMAP.md` (seção de testes) e os testes em `tests/AgenciaOS.Tests/TestesMultiTenant*.cs`.
