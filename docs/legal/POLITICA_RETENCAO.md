# Política de Retenção de Dados (minuta)

> **Minuta para revisão jurídica.** Prazos são sugestões técnicas; confirme com advogado(a) e contador(a). Versão 2026-10-30.

| Dado | Prazo de guarda | Motivo | Depois do prazo |
|---|---|---|---|
| Dados da agência ativa (clientes, tarefas, conteúdos, arquivos, financeiro) | Enquanto o contrato estiver ativo | Execução do contrato | — |
| Agência suspensa por inadimplência | Dados preservados integralmente por 90 dias após a suspensão | Permitir regularização sem perda | Aviso por e-mail; depois, tratado como cancelamento |
| Agência cancelada | 90 dias para exportação | Portabilidade | Exclusão ou anonimização (rotina a implementar na Fase 6) |
| Registros de acesso (login, IP, data/hora) | 6 meses | Marco Civil da Internet, art. 15 | Exclusão |
| Registros de auditoria administrativa | 5 anos | Exercício regular de direitos / segurança | Exclusão |
| Aceites de Termos e Política | Enquanto houver relação + 5 anos | Prova do contrato e do consentimento | Exclusão |
| Cobranças, pagamentos e notas | 5 anos (prazo fiscal) | Obrigação legal | Exclusão |
| Eventos de webhook de pagamento | 1 ano | Conciliação e suporte | Exclusão |
| Pedidos de titulares e respostas | 5 anos | Comprovação de atendimento | Exclusão |
| Tokens de integração (Meta, TikTok, Google) | Até a desconexão ou exclusão do cliente | Necessidade | Apagados imediatamente |
| Backups | 30 dias (rotação) | Recuperação de desastre | Sobrescritos |
| Usuário anonimizado | Registro técnico sem dados pessoais mantido | Integridade do histórico | — |

## Como o sistema aplica hoje (Fase 5)
- Suspensão e cancelamento **não apagam** dados (testado).
- Exclusão sob demanda: anonimização de usuário e de cliente (painel da agência e painel global), mantendo lançamentos financeiros e aceites sem identificação.
- Exportação completa da agência a qualquer momento.

## A implementar (Fase 6)
- Rotina automática que, após os prazos acima, exclui/anonimiza dados de agências canceladas e limpa registros de acesso e eventos de webhook antigos, com relatório para o superadmin.
