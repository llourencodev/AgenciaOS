# Central de Inteligência de Marketing — Documentação final

## 1. Resumo do que foi implementado

- **Contas e Integrações** (`/Integracoes`): um card por plataforma (Instagram, Facebook, TikTok, Google Analytics 4, Search Console) com logo, descrição, status (Não conectado, Conectando, Conectado, Sincronizando, Token expirado, Permissão revogada, Erro na sincronização), conta conectada, última sincronização, erros, métricas que a API não fornece para aquela conta, variáveis pendentes e botões Conectar/Reconectar, Escolher contas, Atualizar e Desconectar.
- **OAuth 2.0 oficial** de cada provedor, com `state` aleatório em cookie criptografado, HttpOnly, amarrado ao usuário/cliente/plataforma, válido por 10 minutos e de uso único; PKCE S256 no Google. O AgenciaOS nunca recebe senha de rede social.
- **Seleção de conta/página/propriedade** após a autorização (validada no servidor contra a lista devolvida pelo provedor). Facebook, GA4 e Search Console aceitam várias; Instagram e TikTok, uma por cliente.
- **Sincronização** em background: carga inicial de 90 dias (30 no Instagram, limite da API), incrementais de 4 dias, intervalos de 6h (Meta, GA4) e 12h (TikTok, Search Console), atualização manual (no máximo 1 a cada 5 min por conta), backoff exponencial (15 min → 24h), respeito a `Retry-After`/HTTP 429, refresh automático de token e histórico de cada execução.
- **Histórico persistido** (métricas diárias, quebras por dimensão, publicações). Dashboards leem só do banco.
- **Visão Geral de Marketing** (`/Marketing`) com filtros Cliente, Período (hoje, 7/15/30/90 dias, este mês, mês anterior, personalizado) e Conta; abas Visão Geral, Instagram, Facebook, TikTok, Website e SEO. Cada indicador mostra valor, variação vs período anterior equivalente, fonte, data da última atualização e metodologia. Métricas com definições diferentes entre redes (alcance, impressões, visualizações, cliques) **não são somadas**: aparecem por fonte.
- **Gráficos** (Chart.js 4.4.1): crescimento de seguidores, alcance por dia, interações por dia, visualizações, tráfego do site, origem dos visitantes, desempenho das publicações, evolução do tráfego orgânico.
- **Desempenho de Conteúdo** (`/Marketing/Conteudo`): cards com thumbnail, legenda, data, plataforma, tipo e métricas; ordenação por visualizações/interações/alcance; filtros por plataforma, tipo, período e conta; melhores conteúdos.
- **Insights de Marketing** (`/Marketing/Insights`): regras sobre os dados reais (maior crescimento, maior engajamento, melhores posts, dia de maior alcance, tráfego do site, canais, SEO, indicadores que melhoraram/pioraram). Interface `IGeradorInsights` pronta para um gerador com IA.
- **Relatórios PDF** (`/Marketing/Relatorios`): cliente, período, plataformas, indicadores e comparação; PDF com resumo, KPIs, gráficos, seções por plataforma, melhores publicações e insights. Tabela já guarda periodicidade e e-mails para envio automático futuro.
- **Carteira da Agência** (`/Marketing/Agencia`, Admin/Equipe): todos os clientes, contas e status, problemas, tokens expirados, métricas consolidadas de 30 dias, filtros e histórico das últimas 100 sincronizações.
- **Isolamento**: `AcessoMarketingService` decide os clientes visíveis. Cliente só vê o próprio cadastro (trocar `clienteId`/`contaId` na URL leva a Acesso Negado). ColaboradorExterno não acessa. Equipe precisa da aba "Marketing" (nova opção em Usuários → abas permitidas).

### Fórmulas
| Indicador | Fórmula |
|---|---|
| Engajamento por seguidores | (interações / seguidores no fim do período) × 100 |
| Engajamento por alcance | (interações / soma do alcance diário) × 100 |
| Crescimento de seguidores | ((atuais − anteriores) / anteriores) × 100 |
| CTR | (cliques / impressões) × 100 |
| Variação | ((atual − anterior) / anterior) × 100 |
| Posição média (SEO) | Σ(posição × impressões) / Σ impressões |
| Taxa de engajamento do site | sessões engajadas / sessões × 100 |

Base zero ou ausente → o indicador fica vazio (sem divisão por zero, sem valor inventado).

## 2. Arquivos criados

```
Models/Marketing.cs
Services/Marketing/MarketingOptions.cs
Services/Marketing/ProtetorTokens.cs
Services/Marketing/OAuthStateService.cs
Services/Marketing/AcessoMarketingService.cs
Services/Marketing/CatalogoMetricas.cs
Services/Marketing/CalculosMarketing.cs
Services/Marketing/PeriodoMarketing.cs
Services/Marketing/SincronizacaoMarketingService.cs   (serviço + fila + worker)
Services/Marketing/IntegracaoMarketingService.cs
Services/Marketing/PainelMarketingService.cs
Services/Marketing/InsightsMarketingService.cs
Services/Marketing/RelatorioMarketingPdfService.cs
Services/Marketing/MarketingServiceCollectionExtensions.cs
Services/Marketing/Provedores/ProvedorBase.cs
Services/Marketing/Provedores/MetaErros.cs
Services/Marketing/Provedores/FacebookProvedor.cs
Services/Marketing/Provedores/InstagramProvedor.cs
Services/Marketing/Provedores/TikTokProvedor.cs
Services/Marketing/Provedores/GoogleProvedores.cs     (GA4 + Search Console)
Controllers/IntegracoesController.cs
Controllers/MarketingController.cs
ViewModels/MarketingViewModels.cs
Views/Integracoes/Index.cshtml, Selecionar.cshtml
Views/Marketing/Index.cshtml, Conteudo.cshtml, Insights.cshtml, Relatorios.cshtml, Agencia.cshtml
Views/Shared/_MarketingNav.cshtml, _MarketingFiltros.cshtml, _MarketingKpi.cshtml, _MarketingGrafico.cshtml, _MarketingPost.cshtml
wwwroot/css/marketing.css
wwwroot/js/marketing.js
Migrations/20261030113623_CentralInteligenciaMarketing.cs (+ Designer)
tests/AgenciaOS.Tests/ (xUnit: 33 testes)
docs/marketing/01-auditoria-e-plano.md, 02-documentacao-final.md
```

## 3. Arquivos alterados (mínimo necessário)

| Arquivo | Alteração |
|---|---|
| `Program.cs` | `AddCentralMarketing(...)` e `UseRateLimiter()` |
| `Data/ApplicationDbContext.cs` | 7 DbSets + configuração das tabelas novas |
| `Migrations/ApplicationDbContextModelSnapshot.cs` | atualizado pelo EF |
| `Views/Shared/_Layout.cshtml` | grupo "Marketing" no menu lateral |
| `Controllers/UsuariosController.cs`, `Views/Usuarios/Index.cshtml` | aba "Marketing" na lista de permissões |
| `AgenciaOS.csproj` | pacote QuestPDF 2026.9.1 e exclusão da pasta `tests/` |
| `.dockerignore` | ignora `tests/` |

## 4. Migrations

`20261030113623_CentralInteligenciaMarketing` — **já aplicada no banco local** (aplica sozinha no startup, como as demais). Só cria tabelas e índices: `ConexoesMarketing`, `ContasMarketing`, `MetricasDiarias`, `MetricasDimensao`, `PublicacoesMarketing`, `HistoricosSincronizacao`, `RelatoriosMarketing`. Em produção (Railway) será aplicada no próximo deploy.

Índices únicos evitam duplicidade: conta+data+métrica, conta+id externo da publicação, cliente+plataforma+id externo da conta.

## 5. Rotas

| Método | Rota | Quem |
|---|---|---|
| GET | `/Integracoes?clienteId=` | agência e cliente |
| POST | `/Integracoes/Conectar` (antiforgery, rate limit) | agência e cliente |
| GET | `/Integracoes/Callback/{instagram\|facebook\|tiktok\|googleanalytics\|searchconsole}` | retorno do OAuth |
| GET/POST | `/Integracoes/Selecionar?conexaoId=` | escolha das contas |
| POST | `/Integracoes/Atualizar` | sincronização manual |
| POST | `/Integracoes/Desconectar` | revoga e apaga tokens (histórico é mantido) |
| GET | `/Marketing` (`aba=geral\|instagram\|facebook\|tiktok\|website\|seo`, `clienteId`, `periodo`, `de`, `ate`, `contaId`) | dashboard |
| GET | `/Marketing/Conteudo` (`plataforma`, `tipo`, `ordem`, `pagina`...) | publicações |
| GET | `/Marketing/Insights` | insights |
| GET/POST | `/Marketing/Relatorios` | gerar relatório |
| GET | `/Marketing/RelatorioPdf/{id}` | download do PDF |
| GET | `/Marketing/Agencia` | só Admin/Equipe |

## 6. Variáveis de ambiente

| Variável | Uso |
|---|---|
| `APP_BASE_URL` | URL pública do sistema (ex.: `https://agenciaos.seudominio.com`). Base das URLs de retorno do OAuth. Sem ela, usa o host da requisição. |
| `META_APP_ID`, `META_APP_SECRET` | App da Meta para **Facebook** |
| `META_GRAPH_VERSION` | opcional, padrão `v23.0` |
| `INSTAGRAM_APP_ID`, `INSTAGRAM_APP_SECRET` | ID e chave secreta **do Instagram** (aparecem no produto Instagram do app da Meta; são diferentes do App ID da Meta) |
| `INSTAGRAM_GRAPH_VERSION` | opcional, padrão `v23.0` |
| `TIKTOK_CLIENT_KEY`, `TIKTOK_CLIENT_SECRET` | App do TikTok for Developers |
| `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET` | Cliente OAuth Web do Google Cloud (GA4 e Search Console) |
| `MARKETING_SYNC_ENABLED` | opcional; `false` desliga o worker (ex.: em uma segunda instância) |

URLs de retorno a cadastrar em cada plataforma (troque pelo seu domínio):
```
https://SEU_DOMINIO/Integracoes/Callback/instagram
https://SEU_DOMINIO/Integracoes/Callback/facebook
https://SEU_DOMINIO/Integracoes/Callback/tiktok
https://SEU_DOMINIO/Integracoes/Callback/googleanalytics
https://SEU_DOMINIO/Integracoes/Callback/searchconsole
```
No Railway: Projeto → serviço → Variables. Localmente: variáveis de ambiente do terminal antes do `dotnet run` (ou `appsettings.Development.json`, que já está no `.gitignore`). Nunca commitar valores reais.

## 7. Meta Developers (Instagram e Facebook)

1. Acesse developers.facebook.com → Meus apps → Criar app → tipo **Empresa** (vincule ao Business Manager da agência).
2. **Facebook**: adicione o caso de uso "Gerenciar tudo na sua Página" / produto *Facebook Login*. Em Configurações do Login, cadastre a URL `.../Integracoes/Callback/facebook` em "URIs de redirecionamento do OAuth válidos". Copie App ID e Chave Secreta (Configurações → Básico) para `META_APP_ID`/`META_APP_SECRET`.
3. **Instagram**: adicione o produto *Instagram* → "Configuração da API com login do Instagram". Copie o **ID do app do Instagram** e a **chave secreta do app do Instagram** para `INSTAGRAM_APP_ID`/`INSTAGRAM_APP_SECRET`. Em "Configurar login comercial", cadastre `.../Integracoes/Callback/instagram`.
4. Preencha em Configurações → Básico: URL da Política de Privacidade, URL de exclusão de dados e ícone (obrigatórios para publicar).
5. Enquanto o app está em modo Desenvolvimento, só funcionam contas com função no app (Funções do app → adicione testadores). Para clientes reais: **Análise do app** pedindo acesso avançado a `pages_show_list`, `pages_read_engagement`, `read_insights`, `instagram_business_basic`, `instagram_business_manage_insights`, mais **Verificação da empresa**. Depois mude o app para modo Ativo.
6. O Instagram exige conta **profissional** (comercial ou criador). Contas pessoais recebem na tela a explicação de como converter.

## 8. TikTok for Developers

1. developers.tiktok.com → Manage apps → Connect an app. Preencha nome, ícone, categoria, termos e política de privacidade.
2. Adicione os produtos **Login Kit** e **Display API**. Em Login Kit → Web, cadastre a Redirect URI `.../Integracoes/Callback/tiktok` (o TikTok exige **HTTPS**, então teste local precisa de um túnel, ex.: ngrok, ou use o ambiente do Railway).
3. Escopos: `user.info.basic`, `user.info.profile`, `user.info.stats`, `video.list`.
4. Copie Client Key e Client Secret para `TIKTOK_CLIENT_KEY`/`TIKTOK_CLIENT_SECRET`.
5. Use o **Sandbox** (com contas-alvo cadastradas) para testar; envie para revisão para liberar a qualquer usuário. Sem `user.info.stats` aprovado, seguidores/curtidas aparecem como indisponíveis.

## 9. Google Cloud (GA4 e Search Console)

1. console.cloud.google.com → crie um projeto.
2. APIs e serviços → Biblioteca → ative **Google Analytics Data API**, **Google Analytics Admin API** e **Google Search Console API**.
3. Tela de consentimento OAuth → tipo Externo → preencha app, domínio, política de privacidade. Escopos: `.../auth/analytics.readonly` e `.../auth/webmasters.readonly`.
4. Credenciais → Criar credenciais → ID do cliente OAuth → **Aplicativo da Web**. Em "URIs de redirecionamento autorizados" cadastre `.../Integracoes/Callback/googleanalytics` e `.../Integracoes/Callback/searchconsole`. Copie ID e chave secreta para `GOOGLE_CLIENT_ID`/`GOOGLE_CLIENT_SECRET`.
5. Atenção: com o app em status **Teste**, só usuários de teste cadastrados conseguem autorizar e o refresh token **expira em 7 dias**. Para produção, publique o app e solicite a verificação do Google (os dois escopos são sensíveis).

## 10. Como testar as conexões

1. Configure as variáveis da plataforma e reinicie o sistema. O card deixa de mostrar "Integração ainda não configurada".
2. Entre como admin → Marketing → Contas e Integrações → selecione o cliente → **Conectar**.
3. Autorize na página oficial da plataforma. Ao voltar, escolha a conta/página/propriedade → **Vincular e sincronizar**.
4. O card passa para "Sincronizando" e depois "Conectado" (alguns minutos). Confira em Carteira da Agência → Histórico de sincronizações (sucesso, registros gravados, métricas indisponíveis).
5. Abra a Visão Geral de Marketing do cliente e a aba da plataforma.
6. Para testar a visão do cliente: em Usuários, crie um login do tipo Cliente, vincule-o no cadastro do cliente e entre com ele. Ele só verá o próprio cliente.
7. Testes automatizados: `dotnet test tests/AgenciaOS.Tests -c Release`.

## 11. O que está efetivamente validado

Validado de verdade, aqui na máquina local:
- Build sem erros; migration aplicada no PostgreSQL local.
- 33 testes automatizados passando: fórmulas e divisão por zero, períodos, criptografia de tokens, state OAuth (válido, forjado, outro usuário, outra plataforma, expirado), sanitização de tokens em mensagens, mapeamento de erros Meta/Google/TikTok, leitura das respostas das APIs, isolamento entre clientes, sincronização sem duplicar, token expirado, backoff, `Retry-After`, refresh de token.
- No navegador: todas as telas novas e as existentes (Dashboard, Clientes, Usuários) respondem 200; `clienteId` de outro cliente → Acesso Negado; POST sem token antiforgery → 400; callback com state forjado ou reutilizado → bloqueado; PDF gerado (application/pdf válido).
- Com IDs de app fictícios: o botão Conectar redireciona para as URLs oficiais corretas de Instagram, Facebook, TikTok e Google (com escopos mínimos, state e PKCE), e o callback chega ao endpoint real de token do Google, que respondeu "OAuth client not found" (esperado com credencial falsa); a mensagem aparece no card e nenhum segredo foi gravado no log.

**Nenhuma integração foi testada ponta a ponta com uma conta real**, porque não há credenciais configuradas. Não considere nenhuma "funcionando" antes do teste do item 10.

## 12. Dependências de aprovação externa

| Plataforma | O que depende de aprovação |
|---|---|
| Instagram / Facebook | Análise do app (acesso avançado às permissões), Verificação da empresa, app em modo Ativo |
| TikTok | Revisão do app para sair do Sandbox; aprovação de `user.info.stats` e `video.list` |
| Google | Publicação e verificação da tela de consentimento (escopos sensíveis) |

## 13. Pendências para produção

1. Criar os apps e configurar as variáveis no Railway, com `APP_BASE_URL` em HTTPS.
2. Fazer o teste real de cada integração (item 10) e passar pelas aprovações (item 12).
3. Publicar Política de Privacidade e URL/instruções de exclusão de dados (exigência da Meta e do TikTok).
4. Proteger as chaves do Data Protection em repouso (hoje ficam na tabela `DataProtectionKeys`, como já era no projeto): usar `ProtectKeysWithCertificate` ou um cofre de chaves. Elas protegem os tokens.
5. O worker roda em cada instância. Se o serviço escalar para mais de uma instância, definir `MARKETING_SYNC_ENABLED=false` nas extras ou adicionar uma trava distribuída.
6. A Meta muda/descontinua métricas com frequência. Os nomes ficam em `FacebookProvedor.MetricasPagina` e `InstagramProvedor.MetricasTotal`; métricas recusadas aparecem como "não disponibilizado pela API" no card.
7. QuestPDF usa a licença Community (gratuita para receita anual abaixo de US$ 1 milhão). Acima disso, é preciso licença paga.
8. Problemas antigos encontrados na auditoria e **não alterados**: `ClientesController.Details` e `ContratosController` não filtram por cliente para usuários do tipo Cliente; o seed de `Configuracao` em `Program.cs` ainda grava "Social Unânime" quando a tabela está vazia.
