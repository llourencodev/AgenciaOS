# Arquitetura multiagências (multi-tenant)

## 1. Modelo escolhido

**Banco único, esquema único, coluna discriminadora `AgenciaId`** em toda tabela que pertence a uma agência. Cada agência é um tenant. Justificativa em `DECISIONS.md` (D01).

O nome segue o padrão em português do código: entidade `Agencia`, coluna `AgenciaId`.

## 2. Entidade `Agencia`

| Campo | Tipo | Observação |
|---|---|---|
| `Id` | int | PK. A agência atual recebe o Id 1 na migração |
| `Nome` | string(150) | razão social / nome |
| `NomeFantasia` | string(150)? | nome comercial |
| `Slug` | string(60), único | usado em URLs e, na Fase 7, subdomínio |
| `Cnpj` | string(18)? | |
| `EmailPrincipal` | string(256)? | |
| `Telefone` | string(30)? | |
| `Status` | enum `StatusAgencia` (Ativa, Suspensa, Cancelada) | bloqueio por agência (usado a partir da Fase 4/5) |
| `SituacaoAssinatura` | enum `SituacaoAssinatura` (Teste, Ativa, PagamentoPendente, Atrasada, Tolerancia, Suspensa, Cancelada) | preenchido pela Fase 4 |
| `PlanoCodigo` | string(50)? | vira FK para `Planos` na Fase 4 |
| `TesteExpiraEm` | DateTime? | Fase 4 |
| `CriadoEm`, `AtualizadoEm` | DateTime | |

Logo, cores e demais preferências continuam em `Configuracao`, agora **uma por agência** (`Configuracao.AgenciaId`). Não dupliquei esses campos em `Agencia` (D04).

## 3. O que recebe `AgenciaId`

| Tabela | Como |
|---|---|
| `Clientes`, `Conteudos`, `Tarefas`, `Arquivos`, `Financeiros`, `Contratos`, `DatasComemorativas`, `Configuracoes`, `ConexoesMarketing`, `ContasMarketing`, `RelatoriosMarketing` | coluna `AgenciaId int NOT NULL` + FK para `Agencias` + índice. Implementam `IPertenceAgencia` |
| `AspNetUsers` (`Usuario`) | `AgenciaId int NULL`. `NULL` = usuário da plataforma (superadministrador) |
| `ComentariosConteudo`, `HistoricosConteudo` | sem coluna: filtrados pela navegação `Conteudo.AgenciaId` |
| `MetricasDiarias`, `MetricasDimensao`, `PublicacoesMarketing`, `HistoricosSincronizacao` | sem coluna: filtrados pela navegação `Conta.AgenciaId` (tabelas de alto volume, sempre acessadas a partir da conta) |
| `Notificacoes` | sem coluna: já filtradas pelo dono (`UsuarioId`), e o dono pertence a uma agência |
| Identity (roles, claims, tokens), `DataProtectionKeys` | globais |

## 4. Camadas de isolamento

```
Requisição ─► Autenticação (cookie) ─► TenantMiddleware ─► Filtro de permissões ─► Controller ─► EF Core
                                          │                     │                               │
                                          │ resolve AgenciaId   │ abas/roles/superadmin         │ filtros globais (leitura)
                                          │ pelo usuário logado │                               │ guarda no SaveChanges (escrita)
                                          ▼                     ▼                               ▼
                                     ITenantContext        PermissaoModuloFilter          ApplicationDbContext
```

### 4.1 Tenant resolution (`TenantMiddleware`)

- Roda depois de `UseAuthentication`.
- Lê o `NameIdentifier` do cookie e busca `AspNetUsers.AgenciaId` no banco (uma consulta por PK por requisição). **Nunca** aceita AgenciaId de rota, query string, formulário ou cabeçalho.
- Se o usuário pertence a uma agência → `ITenantContext` em modo **Agência(id)**.
- Anônimo ou superadministrador → modo **Nenhum** (sem tenant).
- Na Fase 7, o domínio só servirá para escolher a marca da tela de login; a agência efetiva continua vindo do usuário autenticado, e um usuário de outra agência acessando pelo domínio errado é recusado.

### 4.2 Tenant context (`ITenantContext`, escopo da requisição)

| Modo | Leitura de tabelas da agência | Escrita |
|---|---|---|
| `Nenhum` (padrão) | nenhuma linha (falha fechada) | proibida em entidades da agência; permitida em `Usuario` (login, lockout) |
| `Agencia(id)` | só linhas da agência `id` | carimbo automático + verificações (4.4) |
| `Sistema` | tudo | livre. Usado só por código interno: worker de marketing, seed, superadmin criando agência. Ativado explicitamente com `tenant.UsarSistema()` |

Um `ApplicationDbContext` criado sem `ITenantContext` (testes antigos, `dotnet ef`) se comporta como `Sistema`. No app web o DI sempre injeta o contexto, então nenhuma requisição cai nesse caso.

### 4.3 Filtros globais de consulta (EF Core `HasQueryFilter`)

Para toda entidade `IPertenceAgencia`: `e => SemFiltroTenant || e.AgenciaId == AgenciaAtualId`. Os filhos sem coluna usam a navegação (`c => ... || c.Conteudo.AgenciaId == AgenciaAtualId`). `Find`, `Include`, `Count`, `Sum`, `ExecuteUpdate` e `ExecuteDelete` respeitam o filtro, então `db.Tarefas.FindAsync(idDeOutraAgencia)` devolve `null` → 404.

`Usuario` **não** tem filtro global (o Identity consulta usuários antes do login). Toda listagem de usuários usa `.DaAgencia(tenant)` explicitamente, e a guarda de escrita cobre o resto.

### 4.4 Guarda de escrita (`SaveChanges`)

Executada em todo `SaveChanges/SaveChangesAsync` no modo Agência:

1. **Carimbo**: entidade nova sem `AgenciaId` recebe a agência atual. Usuário novo sem agência também.
2. **AgencyId forjado**: entidade nova com `AgenciaId` diferente da atual → exceção (`AcessoEntreAgenciasException`).
3. **Troca de dono**: alterar `AgenciaId` de um registro existente → exceção.
4. **Registro de outra agência**: para toda entidade modificada/excluída, confere no banco (ignorando filtros) que o Id pertence à agência atual. Pega o caso `db.Update(entidadeDoFormulario)` com Id forjado.
5. **Referências cruzadas**: para toda chave estrangeira cujo alvo é uma entidade da agência ou um usuário (`ClienteId`, `ResponsavelId`, `UsuarioClienteId`, `ConteudoId`...), confere que o alvo pertence à mesma agência. Impede, por exemplo, criar uma tarefa da Alpha apontando para um cliente ou responsável da Beta.

No modo `Nenhum`, qualquer escrita em entidade da agência gera exceção. No modo `Sistema` a guarda não roda.

A exceção (`AcessoEntreAgenciasException`) é convertida em **403** pelo `AcessoEntreAgenciasExceptionFilter` e registrada em log de aviso (usuário, caminho, motivo), servindo de sinal de tentativa de acesso indevido.

### 4.5 Autorização dentro da agência

| Perfil | Role | Regra |
|---|---|---|
| Superadministrador da plataforma | `SuperAdmin`, `AgenciaId = NULL` | Só acessa `/Plataforma`. Não entra nos módulos das agências. Provisionado por variável de ambiente no startup, nunca por cadastro |
| Administrador da agência | `Admin` | Tudo dentro da própria agência |
| Equipe (colaborador interno) | `Equipe` | Módulos em `AbasPermitidas` (vazio = todos), validado no servidor |
| Colaborador externo | `ColaboradorExterno` | Tarefas atribuídas a ele e clientes dessas tarefas |
| Cliente da agência | `Cliente` + `Clientes.UsuarioClienteId` | Só o próprio cliente: dashboard, calendário, arquivos, tarefas do próprio cliente, marketing próprio. Sem vínculo → não vê nada |

Implementação: `AcessoService` (generaliza o `AcessoMarketingService`) devolve o escopo do usuário (agência inteira, um cliente, ou só tarefas atribuídas), usado por todos os controllers. `PermissaoModuloFilter` (filtro global MVC) aplica `AbasPermitidas` e separa superadmin de agência.

### 4.6 Uploads e downloads

Implementado na Fase 2 (`Services/Armazenamento/`):

- `IArmazenamentoArquivos` com dois provedores: `ArmazenamentoS3` (Cloudflare R2, Amazon S3 ou compatível; bucket privado) e `ArmazenamentoLocal` (pasta `App_Data/storage`, fora de `wwwroot`). Escolha por `STORAGE_PROVIDER`.
- Chaves: `agencies/{agenciaId}/clients/{clienteId}/{aaaa}/{mm}/{guid}.ext` e `agencies/{agenciaId}/brand/logo-{guid}.ext`. Toda chave é validada (`ChavesArmazenamento.Validar`) contra path traversal e precisa começar em `agencies/{n}/`.
- Upload (`ServicoArquivos`): lista branca de extensões, assinatura binária compatível com a extensão, bloqueio de executáveis (PE, ELF, Mach-O, scripts), limite real de bytes (não o declarado), MIME definido pelo servidor, nome de exibição sanitizado, SHA-256 gravado no banco.
- Download (`/Arquivos/Baixar/{id}`): carrega o registro pelos filtros de agência + `AcessoService` (cliente só o próprio). Com S3/R2 redireciona para URL assinada de 5 min (`STORAGE_URL_TTL_SECONDS`); no modo local transmite o arquivo. Sempre `attachment` exceto imagens raster/vídeo/PDF pedidos com `inline=true`; SVG nunca inline; `nosniff` e CSP `sandbox`.
- `/uploads/*` não é mais servido como estático (404). Registros antigos sem chave continuam acessíveis só pelo endpoint autenticado até serem migrados.
- Logos: `/marca/{agenciaId}/{nome}` (anônimo, porque a marca aparece em telas públicas), aceita apenas nomes gerados pelo sistema (`logo-<32 hex>.png|jpg|webp`).
- Migração: `dotnet AgenciaOS.dll --migrar-arquivos [--remover-originais]` (`MigracaoArquivosService`), com verificação de hash no destino antes de atualizar o banco e antes de apagar o original.

### 4.7 Processos em segundo plano

O worker de marketing cria escopos com `CriarEscopoSistema()`, que coloca o tenant em modo `Sistema`. Cada conta sincronizada carrega o próprio `ClienteId`/conta, então os dados gravados ficam na agência da conta. Novos jobs devem processar uma agência por vez e, quando agirem em nome de uma agência, usar `tenant.UsarAgencia(id)`.

### 4.8 Relatórios e exportações

Usam as mesmas consultas filtradas (PDF de marketing, relatório financeiro). Nenhum relatório monta SQL manual.

## 5. Como impedir associação com IDs manipulados

- Filtros globais tornam IDs de outra agência invisíveis (404).
- A guarda verifica toda FK que aponta para entidade da agência ou usuário.
- POSTs de edição carregam o registro do banco e copiam só campos permitidos (sem `Update(model)`).
- `AgenciaId` nunca é lido do formulário: os controllers não o expõem, e mesmo que venha no POST a guarda recusa valores diferentes do tenant.

## 6. Limitações conhecidas (Fase 1)

- E-mail é único na plataforma inteira (o `UserName` do Identity é o e-mail). Um mesmo e-mail não pode ser usuário de duas agências. Aceitável para começar; revisar se houver demanda.
- As FKs compostas no banco (ex.: `Tarefas(AgenciaId, ClienteId)` → `Clientes(AgenciaId, Id)`) não foram criadas; a consistência entre agências é garantida pela guarda no `SaveChanges`, que é o único caminho de escrita da aplicação (D06).
- Arquivos privados desde a Fase 2. O provedor vale para a instalação inteira: trocar de `local` para `s3` exige rodar a migração de arquivos (registros guardam `Provedor` para auditoria).
