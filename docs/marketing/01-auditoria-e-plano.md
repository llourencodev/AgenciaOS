# Central de Inteligência de Marketing — Auditoria e Plano Técnico

## 1. Auditoria do AgenciaOS

| Item | O que existe hoje |
|---|---|
| Linguagem / framework | C# / ASP.NET Core MVC (Razor Views), .NET 10 |
| Banco | PostgreSQL via EF Core 10 + Npgsql (SQL Server também suportado no `Program.cs`). Migrations em `Migrations/`, aplicadas no startup (`db.Database.Migrate()`) |
| Conversão de datas | Todo `DateTime` é convertido para UTC por value converter no `ApplicationDbContext` |
| Autenticação | ASP.NET Identity com cookie (`Usuario : IdentityUser`), lockout após 5 tentativas |
| Autorização | Roles `Admin`, `Equipe`, `Cliente`, `ColaboradorExterno` + campo `Usuario.AbasPermitidas` (lista de abas que o usuário vê no menu) |
| Cliente | `Cliente` (Id int). O login do cliente é ligado por `Cliente.UsuarioClienteId`. Isolamento hoje é feito à mão em cada controller (`if usuario.Tipo == Cliente → filtra por cliente.Id`) |
| Área do cliente | `DashboardController` devolve `DashboardCliente.cshtml` para usuários do tipo Cliente; menu lateral é o mesmo, filtrado por role/abas |
| Visual | Bootstrap 5.3 + Bootstrap Icons via CDN, fonte Inter, `wwwroot/css/app.css` com `stat-card`, `stat-grid`, `card`, `page-header`, `filter-bar`, `empty-state`, ícones `ni-*`. Cores vêm de `Configuracao` (white-label) via variáveis CSS `--primary`/`--primary-rgb` |
| Gráficos | Nenhuma biblioteca de gráficos (o relatório financeiro usa tabelas + `window.print()`) |
| PDF | Nenhuma biblioteca |
| Integrações externas | Só `BrasilDatasService` (HttpClient simples). Não há OAuth, HttpClientFactory, nem jobs em background |
| Segredos | Data Protection já configurado e com chaves persistidas no banco (`DataProtectionKeys`) |
| CSRF | `[ValidateAntiForgeryToken]` nos POSTs + header `RequestVerificationToken` para AJAX |
| Testes | Não existe projeto de testes |

Problemas pré-existentes encontrados (fora do escopo, **não vou mexer**, só registrando):
- `ClientesController.Details` e `ContratosController` não filtram por cliente quando o usuário é do tipo Cliente.
- O seed de `Configuracao` em `Program.cs` ainda grava "Social Unânime" quando a tabela está vazia (a marca neutra local atual é preservada porque o seed só roda com a tabela vazia).

## 2. Decisões técnicas

- **Sem trocar tecnologia.** Tudo em MVC + Razor + Bootstrap, no mesmo projeto.
- **Gráficos:** Chart.js 4 via CDN jsDelivr (mesmo padrão do Bootstrap), carregado só nas páginas do módulo.
- **PDF:** QuestPDF (licença Community, gratuita para empresas com faturamento anual < US$ 1 mi) gerando o PDF no servidor.
- **Tokens:** criptografados com o Data Protection já existente (`IDataProtector` com propósito próprio). Nunca vão para views/JSON/logs.
- **OAuth:** `state` aleatório (32 bytes) guardado em cookie protegido, HttpOnly, SameSite=Lax, 10 min, amarrado ao usuário logado e ao cliente; PKCE (S256) no Google. Callback valida state, usuário, cliente e provedor.
- **Sincronização:** `BackgroundService` próprio (sem Hangfire), com fila para sincronização manual/inicial, intervalo por plataforma, backoff exponencial, respeito a HTTP 429/`Retry-After` e refresh automático de token.
- **Dashboards leem só do banco.** Nenhuma chamada externa ao abrir páginas (exceto a tela de seleção de contas logo após o OAuth).
- **Rate limiting:** middleware nativo do ASP.NET Core para os endpoints de conectar/atualizar.
- **Isolamento:** um serviço único (`AcessoMarketingService`) resolve quais clientes o usuário pode ver. Toda consulta do módulo passa por ele. Cliente vê só o próprio; Admin/Equipe veem todos; ColaboradorExterno não acessa o módulo.

## 3. Entidades (nomes em português, seguindo o projeto)

| Entidade | Papel | Chave de unicidade |
|---|---|---|
| `ConexaoMarketing` | Autorização OAuth de um cliente numa plataforma (tokens criptografados, expiração, escopos, status) | (ClienteId, Plataforma) |
| `ContaMarketing` | Conta/página/propriedade vinculada (perfil IG, página FB, conta TikTok, propriedade GA4, site do Search Console) | (ClienteId, Plataforma, IdExterno) |
| `MetricaDiaria` | Histórico diário por conta e métrica (snapshot) | (ContaMarketingId, Data, Metrica) |
| `MetricaDimensao` | Quebras diárias (origem de tráfego, páginas, palavras-chave, país, dispositivo, eventos) | (ContaMarketingId, Data, Dimensao, ValorDimensao, Metrica) |
| `PublicacaoMarketing` | Posts/vídeos com métricas | (ContaMarketingId, IdExterno) |
| `HistoricoSincronizacao` | Log de cada sincronização (sucesso/falha, tentativa, mensagem sem token) | — |
| `RelatorioMarketing` | Configuração de relatório gerado (cliente, período, plataformas, indicadores; preparado para periodicidade/e-mail) | — |

Todas têm `ClienteId` (FK para `Clientes`, cascade) para filtrar e isolar.

## 4. Integrações (APIs oficiais)

| Plataforma | Fluxo | Escopos mínimos |
|---|---|---|
| Instagram | Instagram API com Login do Instagram (contas profissionais), token de longa duração com refresh | `instagram_business_basic`, `instagram_business_manage_insights` |
| Facebook | Facebook Login → token de usuário longo → tokens de página | `pages_show_list`, `pages_read_engagement`, `read_insights` |
| TikTok | Login Kit v2 + Display API | `user.info.basic`, `user.info.profile`, `user.info.stats`, `video.list` |
| GA4 | Google OAuth (offline + PKCE), Admin API para listar propriedades, Data API `runReport` | `analytics.readonly` |
| Search Console | Google OAuth, Search Console API `searchAnalytics.query` | `webmasters.readonly` |

Métrica que a API recusar para aquela conta é registrada como indisponível e a tela mostra a limitação (não é preenchida com zero).

## 5. Telas (menu lateral novo grupo "Marketing")

1. **Visão Geral de Marketing** (`/Marketing`) — filtros Cliente, Plataforma, Período, Conta; abas Visão Geral, Instagram, Facebook, TikTok, Website, SEO; KPIs com variação vs período anterior, fonte e última atualização; gráficos.
2. **Desempenho de Conteúdo** (`/Marketing/Conteudo`) — cards/tabela, ordenação e filtros, melhores conteúdos.
3. **Insights de Marketing** (`/Marketing/Insights`) — análises calculadas a partir dos dados sincronizados.
4. **Relatórios** (`/Marketing/Relatorios`) — gerar e baixar PDF.
5. **Contas e Integrações** (`/Integracoes`) — cards por plataforma com todos os estados.
6. **Carteira da Agência** (`/Marketing/Agencia`, Admin/Equipe) — todos os clientes, contas, problemas, tokens expirados, histórico de sincronizações.

## 6. Ordem de execução

Etapas 2→8 conforme pedido, validando build e app rodando a cada etapa. Documentação final em `docs/marketing/`.
