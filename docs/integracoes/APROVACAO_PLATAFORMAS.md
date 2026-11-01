# Aprovação das integrações: Meta, TikTok e Google

> **Situação em 31/10/2026: nenhuma integração foi submetida nem aprovada.** Este documento prepara a submissão. Só marque como "aprovado" depois da confirmação oficial de cada plataforma.

Todas as integrações usam **somente leitura**, OAuth com `state` assinado + PKCE, tokens criptografados (Data Protection), revogação ao desconectar e exclusão de dados sob demanda. Código: `Services/Marketing/Provedores/`, `Controllers/IntegracoesController.cs`, `Controllers/IntegracoesPublicasController.cs`.

## URLs que os painéis pedem (troque `https://app.seudominio.com.br` pelo `APP_BASE_URL`)

| Finalidade | URL |
|---|---|
| Página inicial do app | `https://app.seudominio.com.br/` |
| Política de Privacidade | `https://app.seudominio.com.br/Account/Privacidade` |
| Termos de Uso | `https://app.seudominio.com.br/Account/Termos` |
| Callback OAuth Instagram | `https://app.seudominio.com.br/Integracoes/Callback/instagram` |
| Callback OAuth Facebook | `https://app.seudominio.com.br/Integracoes/Callback/facebook` |
| Callback OAuth TikTok | `https://app.seudominio.com.br/Integracoes/Callback/tiktok` |
| Callback OAuth Google (GA4) | `https://app.seudominio.com.br/Integracoes/Callback/googleanalytics` |
| Callback OAuth Google (Search Console) | `https://app.seudominio.com.br/Integracoes/Callback/searchconsole` |
| Meta: Data Deletion Request Callback | `https://app.seudominio.com.br/integracoes/meta/exclusao-dados` |
| Meta: Deauthorize Callback | `https://app.seudominio.com.br/integracoes/meta/desautorizar` |
| TikTok: Webhook (authorization.removed) | `https://app.seudominio.com.br/integracoes/tiktok/webhook` |
| Canal do titular (LGPD) | `https://app.seudominio.com.br/Privacidade/Solicitar` |

Os slugs vêm de `PlataformaInfo.Slug` (nome da plataforma em minúsculas).

## Meta (Instagram Login API e Facebook Login)

| Item | Instagram | Facebook (Páginas) |
|---|---|---|
| Escopos | `instagram_business_basic`, `instagram_business_manage_insights` | `pages_show_list`, `pages_read_engagement`, `read_insights` |
| Por quê | ler perfil comercial e métricas (alcance, impressões, engajamento) para relatórios do cliente | listar as páginas que o usuário administra e ler métricas da página |
| Variáveis | `INSTAGRAM_APP_ID`, `INSTAGRAM_APP_SECRET` | `META_APP_ID`, `META_APP_SECRET` |

**Exclusão e desautorização (implementado):** recebe o `signed_request`, valida HMAC-SHA256 com o app secret (Facebook ou Instagram), apaga conexões, contas, métricas e publicações daquele usuário e devolve `{ "url", "confirmation_code" }`. A página `/integracoes/exclusao/{codigo}` mostra a situação sem dados pessoais. A desautorização apaga os tokens e marca a conexão como "permissão revogada". Testado com assinatura válida, inválida e de outro app.

**Passos para a submissão (App Review):**
1. Conta Business verificada (Business Verification) e app em modo "Live".
2. Cadastrar as URLs acima, ícone, categoria e e-mail de contato.
3. Para cada permissão: descrição de uso (texto abaixo) + vídeo.
4. Roteiro do vídeo (gravar em produção ou homologação com dados de teste):
   1. Entrar no AgenciaOS como admin da agência → Marketing → Contas e Integrações.
   2. Escolher um cliente → "Conectar Instagram" → tela de consentimento da Meta mostrando os escopos → autorizar.
   3. Selecionar a conta → mostrar o painel com métricas e publicações (explicar que é só leitura).
   4. Gerar o relatório em PDF.
   5. Mostrar "Desconectar" e "Excluir dados".
5. Texto de uso: *"O AgenciaOS é uma plataforma de gestão para agências de marketing. A agência conecta, com autorização do cliente, a conta comercial do Instagram/página do Facebook do cliente para acompanhar alcance, impressões e engajamento em painéis e relatórios de desempenho enviados ao próprio cliente. Não publicamos conteúdo, não enviamos mensagens e não usamos os dados para publicidade."*

## TikTok (Login Kit + Display API)

| Item | Valor |
|---|---|
| Escopos | `user.info.basic`, `user.info.profile`, `user.info.stats`, `video.list` |
| Por quê | identificar a conta, ler estatísticas da conta e lista de vídeos com métricas para relatórios |
| Variáveis | `TIKTOK_CLIENT_KEY`, `TIKTOK_CLIENT_SECRET` |

**Desautorização (implementado):** webhook `authorization.removed` com verificação do cabeçalho `TikTok-Signature` (HMAC-SHA256 do client secret sobre `"{t}.{corpo}"`, janela de 5 minutos). **Confirmar no painel do TikTok** que o formato de assinatura é este antes de ativar (a implementação segue a documentação de webhooks do TikTok for Developers).

**Submissão:** criar o app, cadastrar domínio verificado, URLs de privacidade/termos, redirect URI, descrição de cada escopo, vídeo de demonstração (mesmo roteiro da Meta, com TikTok). Apps começam em "sandbox" com usuários de teste.

## Google (Analytics Data API e Search Console API)

| Item | GA4 | Search Console |
|---|---|---|
| Escopo | `https://www.googleapis.com/auth/analytics.readonly` | `https://www.googleapis.com/auth/webmasters.readonly` |
| Classificação no Google | escopo sensível | escopo sensível |
| Variáveis | `GOOGLE_CLIENT_ID`, `GOOGLE_CLIENT_SECRET` | (as mesmas) |

**Requisitos atendidos no código:** pedido de um escopo por conexão (mínimo), `access_type=offline` com `prompt=consent`, tratamento de `invalid_grant` (token revogado → "permissão revogada"), revogação em `oauth2.googleapis.com/revoke` ao desconectar, exclusão de dados sob demanda, declaração de Uso Limitado na Política de Privacidade.

**Submissão (OAuth consent screen → Verification):**
1. Domínio verificado no Google Search Console (o mesmo do `APP_BASE_URL`).
2. Tela de consentimento com nome, logo, e-mail de suporte, links de privacidade e termos, domínios autorizados.
3. Justificativa por escopo + vídeo mostrando o fluxo de consentimento (com o client ID visível na URL) e o uso dos dados.
4. Escopos sensíveis não exigem a avaliação de segurança CASA (exigida para escopos restritos), mas a verificação da marca e dos escopos pode levar semanas. Até lá, o app funciona em modo "Testing" com até 100 usuários de teste.

## Checklist antes de submeter
- [ ] `APP_BASE_URL` em produção com HTTPS e domínio próprio.
- [ ] Política de Privacidade e Termos revisados por advogado(a) e publicados.
- [ ] Encarregado de dados preenchido em `/Plataforma/Configuracoes`.
- [ ] Contas de teste para os revisores (agência de demonstração com dados fictícios).
- [ ] Webhooks/callbacks testados no ambiente de homologação com os apps em modo de desenvolvimento.
- [ ] Vídeos gravados para cada plataforma.
