# Auditoria e plano: Agente de Disparo IA e Agente de Atendimento IA

Data: 2026-11-20 · Base auditada: `claude/project-thread-zh74pk` (main + PRs #1 a #5, v0.3.4, 107 testes)

## 1. O que o sistema é hoje

| Item | Situação encontrada |
|---|---|
| Linguagem / runtime | C# / .NET 10 LTS |
| Servidor | ASP.NET Core 10 (minimal APIs, JWT, SignalR `/hubs/crm`, workers em segundo plano), roda como Serviço do Windows |
| App | Avalonia 11.3 (Windows), MVVM CommunityToolkit, 13 telas |
| Banco | EF Core 10: SQLite (padrão, `crm.db`) ou PostgreSQL. Sem migrations: `EnsureCreated` + `SchemaUpgrader` aditivo (só SQLite) |
| WhatsApp | Evolution API v2 (modo Baileys/WhatsApp Web ou WHATSAPP-BUSINESS pela própria Evolution). **Não há integração direta com a Cloud API da Meta** |
| IA | Anthropic Claude (SDK oficial `Anthropic` 13.11), ferramentas internas, `claude-haiku-5-5` / `claude-sonnet-5-5` em negociação |
| Testes | xUnit, 107 testes (domínio, infraestrutura, API ponta a ponta, telas headless, conversas ao vivo opcionais) |
| Build | GitHub Actions `build-windows.yml` gera o instalador Inno Setup a cada push/PR |

## 2. Matriz de funcionalidades

Legenda de prioridade: P1 (Atendimento), P2 (Disparo), P3 (integração entre agentes), P4 (CRM + automações). Complexidade: B baixa, M média, A alta.

### 2.1 Existentes e operacionais (aproveitar)

| Funcionalidade | Onde está | Requisito do escopo atendido |
|---|---|---|
| Fila persistente de envio com idempotência, prioridade, retentativas (até 4, espera exponencial 60/120/240 s), 4xx sem repetir, recuperação de itens "Enviando" após reinício | `OutboundQueueProcessor`, `OutboundQueueItem.IdempotencyKey` (índice único) | 2.5 quase completo |
| `SendGuard`: consentimento, descadastro, janela de 24 h, modelo aprovado fora da janela, campanha ativa, horário e dias, limite diário, intervalo mínimo de 15 min entre abordagens, falhas consecutivas, instância conectada, humano atendendo | `Domain/Services/SendGuard.cs` | 2.2, 2.5, 10, 11 |
| Webhook idempotente (chave única do evento + ID real da mensagem), token em tempo constante, rate limit 600/min | `InboundProcessor`, `/webhooks/evolution` | 10, 12 |
| Importação CSV/XLSX/colar com normalização de DDI/DDD, validação, duplicados, lista de bloqueio, contato importado entra como **Pendente** (não autorizado) | `LeadImportService`, `PhoneNormalizer` | 2.2 (parcial, ver 2.2 abaixo) |
| Consentimento com histórico imutável (origem, evidência, data, quem registrou) e lista global de descadastro | `ConsentRecord`, `OptOut` | 2.2, 11 |
| Detecção de descadastro na resposta (interrompe imediatamente, card em "Não tem interesse", conversa encerrada) | `OptOutDetector`, `InboundProcessor` | 2.6 (parcial) |
| Atendimento IA com ferramentas (planos, qualificação, fatos, etapa, resumo, transferir, encerrar), memória por lead (fatos com origem declarado/inferido/atendente + resumo), últimas 30 mensagens, debounce 6 s, resposta descartada se humano assumir ou chegar mensagem nova | `AiAgent`, `AiResponder`, `AiWorker` | 3.2, 3.3 (parcial) |
| Prompt de sistema editável + regras fixas não editáveis + script sem limite | Configurações de IA (v0.3.4) | 3.1 (parcial), 3.7 |
| Preços só de planos aprovados e versionados; base de conhecimento com fonte e aprovação | `Plan`, `KnowledgeArticle` | 3.7, 7 (parcial) |
| Transferência para humano (IA pausa, prioridade alta), assumir/devolver com ação explícita, bloqueio por conversa, mensagem manual pelo celular pausa a IA | `ConversationService`, `ConversationLocks` | 3.6 (parcial), 4 (parcial) |
| Kanban com auditoria (`StageChanges`), IA só sugere "Negócio fechado" | `KanbanRules` | 5 (parcial) |
| Campanhas: rascunho → iniciar → pausar → retomar → cancelar → concluída; revezamento de versões aprovadas | `CampaignService` | 2.4 (parcial) |
| Modelos de mensagem versionados com aprovação, nome do template do canal oficial e alerta de similaridade (Jaccard ≥ 80%) | `MessageTemplate*`, `TemplateRenderer.Similarity` | 2.3 (parcial) |
| Perfis (Atendente/Supervisor/Administrador), auditoria, segredos criptografados (Data Protection), exportação CSV e JSON do titular, backup, limpeza por retenção | `AuthService`, `SettingsStore`, `PhasesApi` | 11 |
| Simulação de conversa (nada salvo nem enviado) e "Sugerir resposta" | `/api/ai/simulate`, tela de IA | botão "Testar Agente" (parcial: só uma mensagem) |
| Dashboard e relatórios (funil, status de entrega vindos do provedor, mensagens enviadas) | `/api/dashboard`, `/api/reports*` | 8 (parcial) |

### 2.2 Existentes, mas incompletas

| Funcionalidade | O que falta | Prioridade | Complexidade |
|---|---|---|---|
| Configuração da IA | Uma configuração global só. Faltam **vários agentes** com nome, empresa, personalidade, tom, objetivos, produtos, restrições de negociação, critérios de transferência, horário e modelo próprios | P1 | M |
| Perguntas de qualificação | Estão fixas dentro do prompt padrão. Faltam perguntas editáveis sem código e **pontuação explicável** | P1 | M |
| Identificação de intenção | Só descadastro é reconhecido. Faltam as 11 categorias e o fluxo por intenção ("quero falar com uma pessoa" hoje depende do modelo) | P1 | M |
| Transferência humana | Falta atribuir atendente disponível automaticamente, notificação, ficha com resumo/produtos/objeções/pendências e horário da transferência | P1 | M |
| Testar Agente | A simulação aceita só uma mensagem e usa a configuração global. Falta conversa de vários turnos, por agente, mostrando intenção, pontuação e ações | P1 | B |
| Resumo de conversas longas | O resumo existe, mas só é atualizado se o modelo decidir. Falta resumo automático quando a conversa passa de 30 mensagens | P1 | B |
| Contatos | Faltam empresa, segmento comercial e finalidade do consentimento; filtros avançados por perfil; anonimização (hoje só exclusão) | P2 | B |
| Campanha | Falta agente responsável, prompt da campanha, data de início, segmento, condições de pausa (ex.: taxa de falha), acompanhamentos e regras de encerramento | P2 | M |
| Fila por campanha / rate limit por conta | Fila é única com intervalo por instância; falta limite por agente/número | P2 | B |
| Controle de respostas | Resposta não atualiza o estado da campanha nem cancela acompanhamentos pendentes daquele contato (o tipo `Acompanhamento` existe mas nada o agenda) | P2/P3 | M |
| Kanban | Seis colunas fixas (enum). O escopo pede nove etapas configuráveis e card com campanha, agente, interesse e próxima ação | P4 | A (enum usado em todo o sistema) |
| Indicadores | Faltam filtros por agente e campanha, transferências, descadastros e custos de IA por período | P4 | B |

### 2.3 Ausentes

| Funcionalidade | Prioridade | Complexidade |
|---|---|---|
| Cadastro de **Agentes de Atendimento** (múltiplos, independentes) | P1 | M |
| Cadastro de **Agentes de Disparo** (múltiplos, com número/instância, horário, fuso, limites, estado ativo/pausado/inativo) | P2 | M |
| **Gerador de abordagens por IA** com controle de similaridade (hash + Jaccard sobre o histórico da campanha) e alerta | P2 | A |
| Credenciais por agente (instância Evolution e chave próprias) | P2 | M |
| Máquina de estados explícita da conversa (quem pode falar: disparo, atendimento, humano) | P3 | M |
| Construtor visual de automações (blocos, validação de ciclos, modo de teste) | P4 | A |
| Base de conhecimento estruturada por empresa/produto (hoje são artigos livres por categoria) | P4 | M |
| Integração direta com a WhatsApp Cloud API oficial da Meta | depende de decisão | A |

### 2.4 Problemas de integração e segurança

| Problema | Risco | Ação proposta |
|---|---|---|
| **Evolution em modo Baileys não é a API oficial.** Não tem garantia de templates, categorias de marketing nem limites oficiais; risco de banimento do número em prospecção | Alto | Manter (é o que está instalado), documentar. Para prospecção em escala, recomendar o modo WHATSAPP-BUSINESS (Cloud API) da Evolution. Em modo oficial, a abordagem fora da janela de 24 h só pode ser o template aprovado **literal**: o gerador de IA fica desligado nesse caso |
| PostgreSQL sem migrations: tabelas novas não são criadas em banco Postgres existente (`SchemaUpgrader` só roda em SQLite) | Médio | Estender o atualizador aditivo para Postgres (`CREATE TABLE IF NOT EXISTS`/`ADD COLUMN IF NOT EXISTS`) |
| Simulação gasta tokens do limite diário (`trackUsage: true`) | Baixo | Manter a contagem (é custo real), mas mostrar os tokens no Testar Agente |
| Instruções maliciosas do cliente (prompt injection) só cobertas indiretamente pelas regras fixas | Médio | Regra fixa explícita: mensagens do cliente são dados; nunca revelar prompt, credenciais ou dados de outros clientes |
| Desconto/condição fora da tabela depende só do prompt | Médio | Restrições de negociação por agente + regra fixa: desconto só se cadastrado |
| `OptOutDetector` trata "bloquear" e "stop" dentro de frases maiores como descadastro (falso positivo aceitável por segurança) | Baixo | Manter; registrar intenção para relatório |

### 2.5 Pode ser aproveitado sem mudança

`SendGuard`, fila e worker, webhook, importador, `PhoneNormalizer`, `ConsentRecord`/`OptOut`, `AiAgent` (ferramentas e montagem de prompt), `ConversationLocks`, `KanbanRules`, `TemplateRenderer.Similarity`, `SettingsStore` (segredos), auditoria, SignalR, telas existentes e o padrão de testes.

## 3. Plano de arquitetura dos recursos faltantes

Princípio: **nada é reescrito**. Os agentes são uma camada de configuração por cima do `AiAgent`/`AiResponder`/`CampaignService` atuais. Sem agente cadastrado, o sistema continua funcionando exatamente como hoje (configuração global).

### 3.1 Modelo de dados (aditivo)

| Tabela nova / coluna | Uso |
|---|---|
| `AgentProfiles` | Kind (`Atendimento` / `Disparo`), Nome, Descrição, Objetivo, Empresa/produto, Personalidade, Tom, Prompt principal, Produtos e serviços, Respostas permitidas, Restrições de negociação, Critérios de transferência, Modelo, Modelo de negociação, Horário (início/fim/dias), Fuso, Limite diário, Intervalo mínimo, Instância WhatsApp, Status (Ativo/Pausado/Inativo), Agente de atendimento padrão (para o disparo), IsDefault |
| `QualificationQuestions` | AgentId (nulo = todas), Ordem, Pergunta, Chave, Peso, Critério de pontuação, Ativa |
| `Leads` + colunas | `Company`, `Segment`, `ConsentPurpose`, `AttendanceAgentId`, `OriginCampaignId`, `QualificationScore`, `QualificationExplanation`, `LastIntent`, `HandoffAt`, `HandoffSummary` |
| `Campaigns` + colunas | `DispatchAgentId`, `Segment`, `Prompt`, `StartDate`, `AiPersonalization`, `MaxFailureRatePercent` (condição de pausa), `StopOnReply`, `FollowUpDays`, `FollowUpText` |
| `Messages` + coluna | `Intent` (intenção detectada na mensagem recebida) |

### 3.2 Serviços

- `IntentDetector` (domínio, determinístico): descadastro, pedido de humano, sem interesse, preço, compra, reclamação, dúvida técnica, objeção, negociação, interesse, primeiro contato. Roda em todo webhook **antes** da IA. Descadastro e pedido de humano agem na hora, sem depender do modelo. A IA refina com a ferramenta `classificar_intencao`.
- `QualificationScorer` (domínio): pontuação 0–100 explicável, somente com critérios comerciais (experiência, carteira, dor, plano de interesse, prazo, pedido de proposta), cada ponto com a regra que o gerou.
- `AgentResolver`: escolhe o agente de atendimento do lead (lead → campanha de origem → padrão → configuração global).
- `HandoffService`: pausa a IA, atribui o atendente ativo com menos conversas abertas, gera a ficha (resumo, produtos de interesse, objeções e dúvidas pendentes), registra o horário, notifica via SignalR.
- `ApproachGenerator` (P2): gera a variação a partir do modelo aprovado + dados reais do contato; bloqueia invenção (só variáveis conhecidas), checa similaridade contra as últimas mensagens da campanha e cai para o texto aprovado se falhar.
- `ConversationStateMachine` (P3): estados `AguardandoContato → Disparo → Ia(Atendimento) → AtendimentoHumano → Encerrado`, transições permitidas e o dono da conversa; usada pelo `SendGuard` e pelo `AiResponder` para garantir um único agente falando.

### 3.3 API

`/api/agents` (CRUD, admin), `/api/agents/{id}/test` (conversa de vários turnos, nada salvo/enviado), `/api/qualification-questions` (CRUD), `/api/leads/{id}/handoff` (manual), `/api/campaigns/{id}/preview-approaches` (P2).

### 3.4 Telas

Menu novo "Agentes de atendimento" e "Agentes de disparo" (lista + editor + painel **Testar Agente** com chat simulado), aba de perguntas de qualificação, ficha de transferência no popup da conversa, campos novos no editor de campanha.

### 3.5 Ordem de entrega (um PR por etapa, empilhados sobre o PR #5)

1. Auditoria e plano (este documento)
2. **Agente de Atendimento**: perfis, perguntas de qualificação, pontuação, intenções, transferência com ficha, regras de segurança, resumo automático, Testar Agente
3. **Agente de Disparo**: perfis, campos de contato, editor de campanha ampliado, gerador de abordagens com similaridade, limites por agente
4. **Integração**: máquina de estados, resposta cancela acompanhamentos e atualiza campanha, contexto passa ao agente de atendimento
5. **CRM + automações**: etapas configuráveis do Kanban, indicadores com filtros, construtor de automações

## 4. Decisões que dependem do Carlos

- Prospecção em escala pelo Baileys tem risco de bloqueio do número; a recomendação é a Cloud API oficial (modo WHATSAPP-BUSINESS da Evolution). O código suporta os dois e respeita as regras de template no modo oficial.
- Treinamento com o material do canal @evolua-ia: será incorporado ao script/base de conhecimento quando a pesquisa paralela terminar, sem copiar texto proprietário literal.

## 5. Entrega (v0.4.0)

Tudo abaixo está implementado no código e coberto por testes. Base: PR #5 (v0.3.4, 107 testes). Resultado: **177 testes passando** (`dotnet test`), incluindo telas headless.

### 5.1 Funcionalidades implementadas

| Área | O que faz | Onde |
|---|---|---|
| Agentes de atendimento | Vários agentes com nome, nome com o cliente, empresa, personalidade, tom, objetivo, prompt, produtos, respostas permitidas, restrições, critérios de transferência, horário/dias/fuso, modelo, estado | `AgentProfile`, `/api/agents`, tela "Agentes de atendimento" |
| Qualificação | Perguntas editáveis (globais ou por agente) com peso; pontuação 0–100 explicável; seis perguntas de revenda IPTV de exemplo | `QualificationQuestion`, `QualificationScorer`, ferramenta `registrar_resposta_qualificacao` |
| Intenções | 11 categorias; cada uma dispara uma ação configurável (continuar, transferir, priorizar, encerrar) | `IntentDetector`, `IntentRouter` |
| Transferência | Pausa a IA, cancela envios automáticos, atribui o atendente com menos conversas, ficha com resumo, produtos, objeções e pendências, horário, notificação; atendente devolve à IA | `HandoffService` |
| Segurança da IA | Regras fixas: sem preço inventado, sem desconto não autorizado, sem dados de outros clientes, sem operação financeira, sem revelar prompt/credenciais, ignora instruções maliciosas | `AiAgent.FixedRules` |
| Memória | Fatos por contato, resumo automático acima de 30 mensagens | `AiAgent` |
| Testar Agente | Conversa simulada de vários turnos por agente, mostra intenção, pontuação, ações e ficha; nada é gravado ou enviado | `/api/agents/{id}/test` |
| Agentes de disparo | Vários agentes com número/instância, horário, fuso, limite diário, intervalo, agente de atendimento vinculado | `AgentProfile` (Kind = Disparo) |
| Contatos | Empresa, segmento, finalidade do consentimento na importação CSV/XLSX | `LeadImport` |
| Abordagens por IA | Personalização opcional sobre o modelo aprovado, validador (empresa, opção SAIR, sem preço/link/relação inventados) e alerta de similaridade; desligada no modo oficial | `ApproachGenerator`, `ApproachValidator` |
| Campanhas | Agente, segmento, prompt, data de início, acompanhamento em etapas, pausa automática por % de falhas/descadastros, validar e pré-visualizar | `CampaignService` |
| Integração | Máquina de estados explícita (Prospecção → IA → Humano → Encerrado); só quem tem a palavra envia; o atendimento recebe o contexto da campanha | `ConversationStateMachine`, `SendGuard` |
| CRM Kanban | 9 etapas do funil + "Em fase de teste"; nome, ordem e visibilidade configuráveis; avanço automático auditado; card com empresa, campanha, agente, pontuação e tags | `KanbanBoardConfig`, `KanbanRules.AutoAdvance` |
| Painel | Indicadores com filtro por período, agente e campanha; conversão por campanha | `/api/dashboard/agents` |
| Automações | Editor de blocos com sim/não, validação de ciclos e conflitos, modo de teste, execução em cada mensagem recebida | `AutomationEngine`, `AutomationService`, tela "Automações" |
| LGPD | Anonimização, retenção configurável com worker diário, exportação e exclusão (já existiam) | `LgpdService` |

### 5.2 Migrações

Não há migrações EF: o banco é criado por `EnsureCreated` e atualizado pelo `SchemaUpgrader`, que só **adiciona** tabelas e colunas (SQLite e PostgreSQL) e nunca apaga nada. Novas tabelas: `AgentProfiles`, `QualificationQuestions`, `AutomationFlows`. Novas colunas em `Leads`, `Messages`, `Campaigns` e `OutboundQueue`. Novos valores de enum (etapas 7–11, modo `Prospeccao = 5`) são aditivos. Para desfazer basta voltar o executável: as colunas novas são ignoradas pela versão anterior.

### 5.3 Dependências e variáveis de ambiente

Nenhuma dependência nova. Nenhuma variável de ambiente nova: chaves (Anthropic, Evolution) continuam criptografadas em Configurações. Ajustes novos ficam na tabela de configurações: `kanban.stages`, `lgpd.retention.days`, `qualification.seeded`.

### 5.4 Configuração

1. Atualize o servidor e o app (instalador 0.4.0). O banco é atualizado sozinho na primeira execução.
2. **Agentes de atendimento**: revise o agente "Atendimento IPTV (padrão)", ajuste as perguntas de qualificação e use **Testar Agente**.
3. **Agentes de disparo**: crie um agente com o número/instância, horário e limites, e vincule o agente de atendimento.
4. **Mensagens**: aprove a abordagem e as 3 versões de "Acompanhamento revenda (3 etapas)".
5. **Campanhas**: escolha o agente, valide, pré-visualize e inicie.
6. **CRM Kanban → Etapas**: renomeie (ex.: "Novo contato", "Venda concluída") ou oculte etapas.
7. **Automações**: crie fluxos, teste e ative.
8. **Contatos**: retenção LGPD em `PUT /api/lgpd/retention?days=N` (0 desliga, mínimo 30).

### 5.5 Pendências e limites

- Prospecção em volume pelo Baileys arrisca bloqueio do número; a recomendação continua sendo a Cloud API oficial (modo WHATSAPP-BUSINESS).
- Similaridade usa comparação de palavras (Jaccard), não embeddings.
- Tokens de IA são contados por dia para o sistema todo, não por agente ou campanha; custo em reais não é estimado.
- "Aguardar resposta" nas automações encerra a execução; o fluxo recomeça do início na próxima mensagem (não guarda a posição).
- Nomes de etapa personalizados aparecem no quadro; outras telas mostram o nome padrão.
- A retenção LGPD ainda não tem tela própria (só a API).
- Os roteiros de venda IPTV seguem os princípios do canal @evolua-ia, mas os vídeos não traziam scripts prontos; revise os textos do agente padrão.
