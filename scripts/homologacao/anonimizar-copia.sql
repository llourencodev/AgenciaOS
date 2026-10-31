-- Anonimiza uma CÓPIA restaurada do banco de produção para uso em homologação (Staging).
-- NUNCA rode no banco de produção. O script se recusa a rodar sem a confirmação abaixo e em bancos com "prod" no nome.
--
-- Uso (depois de restaurar o backup no banco de homologação):
--   psql "$HOMOLOGACAO_DATABASE_URL" -v ON_ERROR_STOP=1 -c "SET agenciaos.confirmar_anonimizacao = 'sim';" -f scripts/homologacao/anonimizar-copia.sql
--   (ou, numa sessão: SET agenciaos.confirmar_anonimizacao = 'sim'; \i scripts/homologacao/anonimizar-copia.sql)
-- Depois, para entrar: dotnet AgenciaOS.dll --definir-senha <email-anonimizado> <senha>  (bloqueado em produção)

DO $$
BEGIN
  IF current_setting('agenciaos.confirmar_anonimizacao', true) IS DISTINCT FROM 'sim' THEN
    RAISE EXCEPTION 'Confirme com: SET agenciaos.confirmar_anonimizacao = ''sim'';';
  END IF;
  IF current_database() ILIKE '%prod%' THEN
    RAISE EXCEPTION 'Recusado: o banco % parece ser de produção.', current_database();
  END IF;
END $$;

BEGIN;

-- Usuários: e-mail previsível por agência/papel, sem senha, sem 2FA, sem telefone
UPDATE "AspNetUsers" SET
  "Nome" = 'Usuário ' || left("Id", 8),
  "Sobrenome" = NULL,
  "Email" = 'usuario-' || left("Id", 8) || '@homologacao.invalid',
  "NormalizedEmail" = upper('usuario-' || left("Id", 8) || '@homologacao.invalid'),
  "UserName" = 'usuario-' || left("Id", 8) || '@homologacao.invalid',
  "NormalizedUserName" = upper('usuario-' || left("Id", 8) || '@homologacao.invalid'),
  "PhoneNumber" = NULL,
  "Foto" = NULL,
  "PasswordHash" = NULL,
  "SecurityStamp" = md5(random()::text),
  "ConcurrencyStamp" = md5(random()::text),
  "TwoFactorEnabled" = false,
  "AccessFailedCount" = 0,
  "LockoutEnd" = NULL;
DELETE FROM "AspNetUserTokens";   -- chaves de autenticador e códigos de recuperação
DELETE FROM "AspNetUserLogins";
DELETE FROM "AspNetUserClaims";

-- Agências e clientes
UPDATE "Agencias" SET "Cnpj" = NULL, "Telefone" = NULL, "EmailPrincipal" = 'agencia-' || "Id" || '@homologacao.invalid';
UPDATE "Clientes" SET
  "NomeEmpresa" = 'Cliente ' || "Id", "Responsavel" = 'Responsável ' || "Id",
  "Email" = 'cliente-' || "Id" || '@homologacao.invalid', "Telefone" = NULL, "Instagram" = NULL,
  "Observacoes" = NULL, "Estrategia" = NULL, "Objetivos" = NULL, "Logo" = NULL;

-- Conteúdo livre que pode conter dados pessoais
UPDATE "Contratos" SET "Conteudo" = '[conteúdo removido na homologação]', "Observacoes" = NULL;
UPDATE "ComentariosConteudo" SET "Texto" = '[comentário removido na homologação]';
UPDATE "Financeiros" SET "Observacoes" = NULL;
DELETE FROM "Notificacoes";
DELETE FROM "ConvitesUsuario";

-- Integrações: tokens de produção nunca vão para homologação (apaga contas, métricas e publicações em cascata)
DELETE FROM "ConexoesMarketing";
-- Chaves de criptografia: homologação gera as suas
DELETE FROM "DataProtectionKeys";

-- Cobrança: ids do gateway de produção não podem casar com o sandbox
UPDATE "Assinaturas" SET "GatewayClienteId" = 'hmg_' || "Id", "GatewayAssinaturaId" = 'hmg_sub_' || "Id";
UPDATE "Pagamentos" SET "GatewayPagamentoId" = 'hmg_pay_' || "Id", "UrlFatura" = NULL;
UPDATE "EventosWebhook" SET "Payload" = '{}';

-- LGPD e auditoria
UPDATE "ConsentimentosTermos" SET "Ip" = NULL, "UserAgent" = NULL;
UPDATE "RegistrosAuditoria" SET "UsuarioEmail" = NULL, "Ip" = NULL, "Detalhes" = NULL;
UPDATE "SolicitacoesPrivacidade" SET
  "NomeTitular" = 'Titular ' || "Id", "EmailTitular" = 'titular-' || "Id" || '@homologacao.invalid',
  "Descricao" = '[removido na homologação]', "Resposta" = NULL, "Ip" = NULL;

COMMIT;

SELECT 'Anonimização concluída' AS resultado,
       (SELECT count(*) FROM "AspNetUsers" WHERE "Email" NOT LIKE '%@homologacao.invalid') AS emails_reais_restantes;
