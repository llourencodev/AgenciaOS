# Arquitetura atual do AgenciaOS

> Levantamento feito em 30/10/2026 (Fase 0), lendo o código-fonte. Este documento descreve o sistema **antes** da Fase 1 (multi-tenancy). As mudanças da Fase 1 estão em `MULTITENANCY_DESIGN.md` e `DECISIONS.md`.

## 1. Stack

| Item | Tecnologia |
|---|---|
| Linguagem | C# 13 / .NET 10 |
| Web | ASP.NET Core MVC (controllers + Razor Views), sem API REST separada |
| Front-end | Razor + Bootstrap 5.3 (CDN) + Bootstrap Icons + JS próprio (`wwwroot/js`), Chart.js no marketing |
| Banco | PostgreSQL (Npgsql). Existe um ramo `UseSqlServer` em `Program.cs` que só é usado se a connection string não parecer Postgres (legado) |
| ORM | Entity Framework Core 10, migrations em `Migrations/` aplicadas automaticamente no startup (`db.Database.Migrate()`) |
| Autenticação | ASP.NET Core Identity com cookie (`Usuario : IdentityUser`), roles `Admin`, `Equipe`, `Cliente`, `ColaboradorExterno` |
| Proteção de dados | Data Protection com chaves persistidas no banco (`DataProtectionKeys`) |
| PDF | QuestPDF (relatórios de marketing) |
| Deploy | Railway, via `Dockerfile` (SDK 10 → aspnet 10), healthcheck em `/health`, `railway.toml` |
| Testes | `tests/AgenciaOS.Tests` (xUnit + SQLite em memória), 33 testes, todos do módulo de marketing |

## 2. Estrutura de pastas

```
AgenciaOS/
├─ Controllers/        12 controllers MVC + Integracoes/Marketing
├─ Data/               ApplicationDbContext + factory de design-time
├─ Models/             Entidades EF (português)
├─ ViewModels/         DTOs de tela
├─ Services/           NotificacaoService, BrasilDatasService
│  └─ Marketing/       Central de Inteligência de Marketing (provedores OAuth, sync, painéis, PDF)
├─ Views/              Razor, um diretório por controller + Shared/_Layout
├─ Migrations/         InitialCreate, DataProtectionKeys, CentralInteligenciaMarketing
├─ wwwroot/            estáticos + uploads/ (arquivos de clientes) + brand/ (logo)
├─ tests/AgenciaOS.Tests
└─ docs/               marketing/ e architecture/
```

## 3. Modelo de dados (antes da Fase 1)

Não existe entidade de agência. O sistema assume **uma única agência implícita**.

| Entidade | Tabela | Chaves/relacionamentos relevantes |
|---|---|---|
| `Usuario` | `AspNetUsers` | `Tipo` (enum espelha a role), `AbasPermitidas` (lista CSV de módulos) |
| `Cliente` | `Clientes` | `UsuarioClienteId` → Usuario (login do cliente). **Nada no sistema preenche esse campo** |
| `Conteudo` | `Conteudos` | `ClienteId` (cascade), `CriadoPorId` → Usuario. É o "calendário editorial" |
| `ComentarioConteudo`, `HistoricoConteudo` | idem | `ConteudoId`, `UsuarioId` |
| `Tarefa` | `Tarefas` | `ClienteId?`, `ResponsavelId?`, `CriadoPorId?` |
| `Arquivo` | `Arquivos` | `ClienteId`, `Url` = caminho público `/uploads/{clienteId}/{guid}.ext` |
| `Financeiro` | `Financeiros` | `ClienteId?`, `ResponsavelId?`, receitas e despesas no mesmo registro (`Tipo`) |
| `Contrato` | `Contratos` | `ClienteId` |
| `DataComemorativa` | `DatasComemorativas` | sem dono |
| `Configuracao` | `Configuracoes` | linha única: nome, cores, logo (`/brand/logo.ext`) |
| `Notificacao` | `Notificacoes` | `UsuarioId` |
| Marketing: `ConexaoMarketing`, `ContaMarketing`, `MetricaDiaria`, `MetricaDimensao`, `PublicacaoMarketing`, `HistoricoSincronizacao`, `RelatorioMarketing` | idem | tudo pendurado em `ClienteId`; métricas/publicações também em `ContaMarketingId` |

Todos os `DateTime` passam por um conversor UTC global no `OnModelCreating`.

## 4. Autenticação

- Login por e-mail/senha (`AccountController.Login`), lockout de 5 tentativas / 5 min, cookie de 7 dias com sliding expiration.
- Senha mínima de 8 caracteres, sem exigência de dígito/símbolo.
- `RequireConfirmedAccount = false`; não há confirmação de e-mail, recuperação de senha, convite nem 2FA.
- Não existe cadastro público. Usuários são criados por um Admin em `UsuariosController.Create`, que define a senha.
- Seed no startup: cria `admin@agencia.com` com `ADMIN_PASSWORD` **ou a senha padrão `Admin@2025!`**.

## 5. Autorização

- Roles via `[Authorize(Roles = ...)]` por controller/ação.
- `AbasPermitidas` (módulos liberados por usuário) **só é aplicado no menu** (`_Layout.cshtml`, `TemAcesso`). O servidor não verifica.
- Usuário `Cliente` é identificado por `Clientes.UsuarioClienteId == usuario.Id`. Como nenhuma tela grava esse vínculo, um usuário Cliente cai sempre no caso "sem cliente vinculado".
- O módulo de marketing é a exceção: tem um ponto único de isolamento (`AcessoMarketingService`) e testes.

## 6. Módulos

| Módulo | Controller | Quem acessa | Observações |
|---|---|---|---|
| Dashboard | `DashboardController` | todos | Cliente vê painel próprio; demais veem números globais (inclusive receita) |
| Clientes | `ClientesController` | todos autenticados (lista e detalhes); Admin/Equipe criam/editam | detalhe carrega conteúdos, tarefas e arquivos do cliente |
| Calendário editorial | `CalendarioController` | todos (leitura, comentários); Admin/Equipe criam/alteram status | endpoint JSON `Eventos` |
| Tarefas | `TarefasController` | todos (lista); Admin/Equipe criam/editam; ColaboradorExterno só vê as próprias | recorrência gera N registros |
| Arquivos | `ArquivosController` | todos (lista e upload); Admin/Equipe excluem | grava em `wwwroot/uploads` |
| Financeiro | `FinanceiroController` | Admin/Equipe | parcelas e contas fixas geram N registros |
| Contratos | `ContratosController` | Admin/Equipe | |
| Datas comemorativas | `DatasComemorativasController` | todos | importação de datas do Brasil |
| Usuários | `UsuariosController` | Admin | |
| Configuração / identidade visual | `ConfiguracaoController` | Admin (`Tema` é anônimo) | logo em `wwwroot/brand` |
| Notificações | `NotificacoesController` | dono da notificação | já filtra por `UsuarioId` |
| Marketing / Integrações | `MarketingController`, `IntegracoesController` | Admin/Equipe com aba Marketing; Cliente só o próprio | ver `docs/marketing/02-documentacao-final.md` |

## 7. Arquivos

- Upload (`ArquivosController.Upload`): valida extensão (lista branca) e tamanho (50 MB). Não valida MIME real. Grava em `wwwroot/uploads/{clienteId}/{guid}{ext}`.
- Download: **não existe endpoint**. O arquivo é servido como estático por `UseStaticFiles`, sem autenticação. Quem tiver a URL baixa.
- Logo da agência: `wwwroot/brand/logo{ext}` e `logo-marca{ext}`, sem validação de extensão (aceita SVG → XSS armazenado possível).
- No Railway o disco do container é efêmero a cada deploy, salvo volume montado.

## 8. Marketing e integrações

- OAuth com `state` assinado em cookie + PKCE, tokens criptografados com Data Protection (`ProtetorTokens`).
- `SincronizacaoMarketingWorker` (BackgroundService) varre contas vencidas a cada 5 min usando `IServiceScopeFactory` — roda fora de qualquer requisição/usuário.
- Isolamento por cliente feito por `AcessoMarketingService`.

## 9. Configurações e identidade visual

Tabela `Configuracoes` com uma única linha, lida pelo `_Layout.cshtml` a cada requisição (`FirstOrDefault`). O seed grava a identidade "Social Unânime" na primeira execução.

## 10. Deploy (Railway)

- Build do `Dockerfile`; `CMD dotnet AgenciaOS.dll --urls http://0.0.0.0:$PORT`.
- Connection string: `ConnectionStrings__DefaultConnection` ou `DATABASE_URL` (convertida de URL para key=value com SSL obrigatório).
- Migrations rodam no startup, sem etapa separada nem backup prévio.
- Sem pipeline de CI: não há testes antes do deploy.
- HTTPS terminado no proxy do Railway (`UseForwardedHeaders`).

## 11. Testes

`tests/AgenciaOS.Tests` referencia o projeto web, usa SQLite em memória com o modelo real (`EnsureCreated`). Cobrem cálculos, provedores com HTTP simulado, OAuth state, isolamento do marketing. Rodar com `dotnet test tests/AgenciaOS.Tests -c Release` (o bin de Debug fica travado quando o servidor de dev está rodando).

## 12. Migrations e atualizações

`Database.Migrate()` em todo startup. Não há separação entre "aplicar migration" e "subir aplicação", nem script de rollback. Local: Postgres em Docker (`agenciaos-postgres`, porta 5434).

## 13. Dependências entre módulos que afetam a migração

- Quase tudo depende de `Cliente`: conteúdos, tarefas, arquivos, contratos, financeiro e todo o marketing. Se `Cliente` tiver dono (agência), os filhos herdam o dono por consistência.
- `Usuario` é referenciado por tarefas, financeiro, conteúdos, comentários, notificações e marketing.
- `_Layout.cshtml` consulta `Configuracoes` e `UserManager` diretamente: qualquer filtro de tenant afeta todas as telas, inclusive o login (anônimo).
- Identity (`UserManager`/`SignInManager`) consulta `AspNetUsers` antes de existir usuário autenticado; um filtro global em `Usuario` quebraria o login.
- O worker de marketing roda sem usuário; filtros por tenant precisam de um modo "sistema" explícito.
