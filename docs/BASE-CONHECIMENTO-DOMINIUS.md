# Base de conhecimento comercial Dominius Play — entrega v0.4.1

Implementação do "Prompt Mestre" (novembro de 2026) sobre o sistema existente, sem reconstruir nada:
mesmos agentes, mesma máquina de estados da conversa, mesmo banco (atualização aditiva), mesmas telas.

## 1. Onde estava cada coisa (análise antes de implementar)

| Componente | Onde fica | O que foi feito |
|---|---|---|
| Prompt do agente | `AiAgent.DefaultSystemPrompt` + `FixedRules` + prompt por agente (`AgentProfile.Prompt`) | Mantidos. A base entra como **bloco separado** a cada mensagem (`KnowledgeBaseService.Brief`). Ajustadas só as regras que conflitavam com o Prompt Mestre (Pix pode ser informado; nunca confirmar pagamento). |
| Conhecimento antigo | `KnowledgeArticles` (artigos do site, aprovação manual) | Preservado, agora na aba "Artigos do site (antigos)". Em conflito, vale a base Dominius Play. |
| Preços | `Plans` (versionados, aprovação) | Tabela oficial nov/2026 aplicada como **novas versões aprovadas**; versões antigas arquivadas (nada apagado). |
| Agente de disparo | `CampaignService`, `ApproachGenerator`, `SendGuard` | Sem mudança de fluxo. Pode receber os fatos oficiais se o administrador liberar o acesso. |
| Agente de atendimento | `AiResponder` → `AiAgent.RunAsync` | Consulta a base a cada mensagem, identifica perfil e gatilho, tem a ferramenta `consultar_base_conhecimento` e passa por uma conferência antes do envio. |
| Histórico das conversas | `Messages`, `LeadFacts` (memória) | Perfil comercial, produto em discussão e ofertas apresentadas viram fatos do lead (`perfil_comercial`, `produto_em_discussao`, `oferta_apresentada`). |
| Transferência humana / descadastro | `IntentDetector`, `IntentRouter`, `HandoffService`, `OptOutDetector` | Reaproveitados (regras fixas, sem IA). |
| Respostas duplicadas Disparo × Atendimento | `ConversationStateMachine` + `SendGuard` | Já existia; agora coberto pelo teste T35. |

## 2. Arquivos

**Criados**
- `src/DominiusPlay.Domain/Entities/KnowledgeBase.cs` — `KbEntry` (ID, categoria, pergunta, variações, resposta, palavras-chave, produtos, prioridade, situação, fonte, precisa confirmação, versão) e `KbEntryRevision` (histórico completo).
- `src/DominiusPlay.Domain/Services/DominiusKnowledge.cs` — as 75 perguntas e respostas.
- `src/DominiusPlay.Domain/Services/Commercial.cs` — `CommercialConfig` (Pix, links, códigos, prazos, regras), `PlanCodes`, `CreditMath`.
- `src/DominiusPlay.Domain/Services/KnowledgeMatcher.cs` — busca semântica leve (sem banco vetorial), marcadores (`{pix}`, `{preco:MENSALISTA_10}`…), validação e histórico.
- `src/DominiusPlay.Domain/Services/CommercialIntelligence.cs` — 5 perfis, gatilhos comerciais, mensagens sugeridas e `ReplySafetyGuard`.
- `src/DominiusPlay.Domain/Services/AttendanceLab.cs` — motor do Laboratório e os 11 perfis de cliente.
- `src/DominiusPlay.Server/Services/KnowledgeBaseService.cs`, `src/DominiusPlay.Server/Endpoints/KnowledgeBaseApi.cs`.
- `src/DominiusPlay.Desktop/ViewModels/KnowledgeBase.cs`, `Views/KbView.axaml(.cs)`, `Views/LabView.axaml(.cs)`.
- `tests/DominiusPlay.Tests/KnowledgeBaseTests.cs` — 35 testes obrigatórios (T01–T35) + 9 de integração.

**Modificados**
- `AiAgent.cs` (bloco da base, ferramenta, conferência da resposta, regras de Pix), `AgentServices.cs` (texto padrão do agente), `ApproachGenerator.cs` (fatos oficiais opcionais), `Program.cs` (registro), `SeedData.cs` (tabela oficial + carga da base), `CrmDbContext.cs`, `SecretStore.cs` (chaves), `PlanRecommender.cs`, `Dtos.cs`, `ApiClient.cs`, `Core.cs` (menu), `ViewLocator.cs`, testes antigos (contagem de planos e ferramentas), versão 0.4.1.

## 3. Banco (migração segura)

Sem migrations destrutivas. Na primeira execução da 0.4.1:
1. `SchemaUpgrader` cria as tabelas `KbEntries` e `KbEntryRevisions` (SQLite e PostgreSQL).
2. `SeedData.EnsureCommercialAsync` (idempotente):
   - grava a configuração comercial padrão e o acesso "Atendimento" (só se não existirem);
   - carrega as 75 perguntas (só se a base estiver vazia — edições do administrador nunca são sobrescritas);
   - aplica a tabela oficial **uma vez** (marca `commercial.table.2026-11`): cria Mensalista 10/100/200/500 e confirma Apontamento 600 (R$ 250), Apontamento Ilimitado (R$ 500) e Franquia (R$ 650) como novas versões aprovadas; arquiva o Mensalista 50 e versões com preço diferente; troca, só no texto padrão do agente, a frase "Nunca gere cobrança nem passe chave Pix" pela regra nova. Tudo registrado em Auditoria.

Contatos, campanhas, mensagens, agentes e configurações existentes não são alterados.

## 4. Como a IA usa a base

A cada mensagem do cliente o servidor (sem custo de IA):
1. detecta intenção, **perfil** (iniciante, revendedor experiente, franquia, amigo, provedor) e **gatilho** (orçamento baixo, achou caro, amigo, já tem painel, marca própria, teste, teste do painel, contratar, Pix, "já paguei", aplicativos, sub-revendedor);
2. busca as 3 respostas oficiais mais próximas, considerando o produto em discussão ("qual o mais barato?" depende do assunto);
3. monta o bloco "BASE DE CONHECIMENTO DOMINIUS PLAY" com a configuração comercial vigente, o perfil, o gatilho (com a mensagem sugerida, uma vez por conversa) e as respostas — que prevalecem sobre o histórico e sobre artigos antigos;
4. o Claude responde (pode chamar `consultar_base_conhecimento`);
5. **conferência** (`ReplySafetyGuard`): se a resposta confirmar pagamento, prometer lucro, oferecer teste do painel, inventar DNS, citar parceiro antigo como ativo, afirmar autorização de conteúdo, falar em créditos ilimitados ou trazer outra chave Pix, ela não sai: vai a resposta oficial da base ou a conversa passa para um humano.

## 5. Telas

- **Base de conhecimento** (menu): abas *Perguntas e respostas* (pesquisar, filtrar, criar, editar, publicar, desativar, excluir, histórico, exportar/importar JSON, prévia com valores preenchidos), *Testar perguntas* (inclui rascunhos), *Comercial* (Pix, formas de pagamento, links, códigos dos apps, parceiros, prazos, regras de créditos e subpainéis, ofertas, modalidade por cliente ativo, quais agentes consultam a base, histórico de alterações) e *Artigos do site (antigos)*.
- **Laboratório IA** (menu): 11 perfis prontos ou conversa livre; mostra mensagem, intenção, perfil, gatilho, pergunta encontrada, resposta, produto, memória, ação sugerida, alertas e encaminhamento humano. Opcionalmente gera a resposta do Claude (passa pela mesma conferência). Nada é enviado e nenhum pagamento é feito.

Leitura: toda a equipe. Alterações: somente Administrador (o atendente recebe 403).

## 6. Testes

`dotnet test`: 221 testes passando (177 anteriores ajustados + 44 novos). Os 35 da parte 13 estão em `KnowledgeBaseTests` com o número no nome (T01…T35).

## 7. Precisa de confirmação de Carlos

1. **Mensalista 50** (R$ 50) não está na tabela nova e foi arquivado. Se ainda vende esse plano, reaprove em Planos.
2. **Modalidade pós-paga por cliente ativo**: cadastrada, mas **desligada** (o Prompt Mestre pede verificar se está vigente). Ligue em Base → Comercial quando confirmar.
3. **Permissão Master** de cada painel: o sistema não tem acesso ao painel Sigma, então a IA sempre diz "se o seu painel for Master". Confirmar quais planos já saem como Master.
4. Respostas marcadas "precisa confirmação" (19, 32, 33, 35, 36, 42, 44, 45, 48, 50, 59, 62, 66, 68, 69, 70): a IA responde com o texto da base e oferece a confirmação da equipe.
5. Taxa de adesão de R$ 60 e faixas por cliente ativo: valores "anteriormente informados".
6. O agente de disparo **não** consulta a base por padrão (só o de atendimento). Libere em Base → Comercial se quiser.

## 8. Configuração manual

Nenhuma obrigatória. Depois de instalar: abra **Base de conhecimento → Comercial**, confira a chave Pix e os links e rode os 11 perfis no **Laboratório IA** antes de ligar a IA para clientes reais.
