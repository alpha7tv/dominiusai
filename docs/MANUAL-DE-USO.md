# Manual de uso do DOMINIUS PLAY AI CRM

Versão 0.4.3 · servidor na VPS · acesso pelo navegador em **https://crm.minhafiberlink.com.br**

Este manual segue a ordem em que você vai usar o sistema: primeiro acesso, ligar a IA e o WhatsApp, preparar agentes e mensagens, importar contatos, disparar campanhas, atender e acompanhar resultados. No fim estão a manutenção da VPS e a solução de problemas.

---

## 1. Como o sistema funciona

| Parte | O que faz |
|---|---|
| **Servidor do CRM** (VPS) | Guarda os dados, conversa com a IA (Anthropic Claude), envia e recebe WhatsApp, roda as filas de envio. Fica ligado 24 horas. |
| **Telas** | Abertas no navegador (Chrome, Edge ou Firefox). Não precisa instalar nada. Todos os atendentes usam o mesmo endereço, cada um com seu usuário. |
| **Evolution API** | O "telefone" do sistema: liga o CRM ao número de WhatsApp. Já existe a sua em `api.minhafiberlink.com.br:8444`, instância `IANOVO`. |
| **IA (Claude)** | Responde os clientes seguindo o agente, a base de conhecimento e a tabela de planos aprovada. Nunca inventa preço nem confirma pagamento. |

Caminho de uma conversa: a **campanha** manda a abordagem → o contato responde → o **agente de atendimento** (IA) conversa, qualifica e move o card no **Kanban** → quando precisa, transfere para um **humano** → o humano fecha a venda ou devolve para a IA.

---

## 2. Primeiro acesso

1. Abra **https://crm.minhafiberlink.com.br**. Na primeira vez o carregamento demora alguns segundos (baixa cerca de 7 MB; depois fica em cache).
2. Como o banco começa vazio, a tela pede para **criar o administrador**: escolha usuário, nome de exibição e uma senha forte (mínimo 8 caracteres). Guarde essa senha: é a conta que pode tudo.
3. Entre com esse usuário.
4. Vá em **Equipe** e crie os outros usuários. Perfis:
   - **Atendente**: conversas, Kanban e contatos.
   - **Supervisor**: tudo do atendente, mais campanhas, mensagens, relatórios e distribuição de conversas.
   - **Administrador**: tudo, inclusive usuários, planos, integrações e IA.
5. Para trocar sua própria senha: **Equipe → Minha senha**. Para desativar alguém, desmarque **Usuário ativo**.

> Se um dia a tela de criar administrador aparecer de novo, o banco foi trocado ou apagado. Não crie outro administrador: veja a seção 14 (restaurar backup).

---

## 3. Ligar a IA (chave da Anthropic)

Menu **Configurações**.

1. Em **Chave de API da Anthropic** cole a chave `sk-ant-...` (pegue em console.anthropic.com → API Keys). Ela fica criptografada no servidor e nunca aparece de novo na tela; aparece só "✓ Chave salva".
2. Clique em **Carregar modelos** para conferir que a chave funciona.
3. Modelos recomendados:
   - **Atendimento**: `claude-haiku-5-5` (rápido e econômico).
   - **Negociação** (quando o card está em "Em negociação"): `claude-sonnet-5-5`.
4. Ajustes úteis:
   - **Nome de exibição da assistente** (ex.: Lia).
   - **Espera para agrupar mensagens**: segundos que a IA espera antes de responder, para juntar mensagens picadas do cliente (5 a 10 s é bom).
   - **Limite diário de consumo (tokens)**: trava de segurança de custo. Quando estoura, a IA para de responder até o dia seguinte.
   - **Mostrar "digitando"**.
5. Ligue **IA atendendo as conversas** e clique em **Salvar configurações da IA**.

Mais abaixo, na mesma tela:
- **Script de atendimento**: o jeito de atender em texto livre (tom, apresentação, oferta principal, objeções, quando transferir). Vale para toda resposta.
- **Prompt de sistema (avançado)**: a base da IA. Só mexa se souber o que está fazendo; existe botão para voltar ao padrão.

Preço **nunca** vem do script: a IA só usa valores aprovados na tela **Planos**.

---

## 4. Ligar o WhatsApp (Evolution API)

Menu **Integrações**.

1. Preencha:
   - **URL do servidor**: o endereço da sua Evolution, `api.minhafiberlink.com.br:8444`, com `http://` ou `https://` conforme ela estiver configurada.
   - **Nome da instância**: `IANOVO`
   - **Chave de API (apikey)**: a chave global ou da instância da Evolution.
   - **Modo de integração**: Baileys (WhatsApp normal, QR Code) ou WHATSAPP-BUSINESS (Cloud API oficial). Para disparo em volume, a Cloud API oficial é a que segue as regras do WhatsApp e corre menos risco de bloqueio.
   - **URL pública deste servidor CRM**: `https://crm.minhafiberlink.com.br`
2. **Salvar configuração**.
3. **Testar conexão**: deve mostrar a instância conectada.
4. **Registrar webhook na Evolution API**: a Evolution passa a mandar as mensagens recebidas para `https://crm.minhafiberlink.com.br/webhooks/evolution` com um token secreto. Sem esse passo o CRM envia mas **não recebe** respostas.
5. **Mensagem de teste**: coloque seu próprio WhatsApp e envie. Responda do celular e confira se aparece em **Histórico de eventos recebidos** e na **Caixa de entrada**.

Se mudar a URL do CRM ou trocar de instância, clique de novo em **Registrar webhook**.

---

## 5. Planos e informações comerciais

### Planos
Menu **Planos**. É a única fonte de preço da IA.
- Para mudar preço: selecione o plano → **Criar nova versão (rascunho)** → confira → **Aprovar versão selecionada**. Só administradores aprovam.
- A IA só oferece planos **aprovados e dentro da validade**.

### Comercial (Pix, links, prazos)
Menu **Base de conhecimento → aba Comercial**: chave Pix, formas de pagamento, site, link do teste, player web, apps (oficial e P2P), apps de Smart TV, parceiros e prazos. A IA pode informar a chave Pix, mas sempre pede para o cliente conferir o recebedor e **nunca confirma pagamento**.

---

## 6. Base de conhecimento

Menu **Base de conhecimento**. São perguntas e respostas oficiais (já vêm 75 sobre a Dominius Play) que a IA consulta antes de responder. O que você publica vale na próxima mensagem, sem reiniciar nada.

Para criar ou editar:
1. **+ Nova** (ou selecione uma existente).
2. Preencha **Pergunta principal**, **Variações** (outras formas do cliente perguntar, uma por linha), **Resposta oficial**, **Categoria**, **Prioridade**, **Palavras-chave**.
3. Na resposta use marcadores para valores que mudam: `{pix}`, `{link_teste}`, `{preco:MENSALISTA_10}`. O quadro **Como a IA recebe** mostra o texto já preenchido.
4. Marque **Precisa de confirmação da equipe** quando a IA deve dizer que vai confirmar com um humano.
5. **Salvar** (fica rascunho) → **Testar antes de publicar** → **Publicar**.

Outros botões: **Desativar**, **Histórico** (versões anteriores), **Exportar**/**Importar…** (cópia da base em arquivo) e **Testar perguntas**, que mostra quais respostas a IA encontraria para uma frase do cliente.

---

## 7. Agentes de atendimento (IA que conversa)

Menu **Agentes de atendimento**. Já existe o agente "Atendimento IPTV (padrão)".

1. Selecione o agente ou clique em **+ Novo agente**.
2. **Perfil**: nome no CRM, nome com o cliente, empresa, objetivos, personalidade, tom, produtos, respostas a objeções, restrições de negociação e **critérios de transferência para humano**.
3. **Prompt principal**: vazio usa o global de Configurações. As regras fixas de segurança valem sempre (não inventa preço, não dá desconto sem autorização, não revela dados de outros clientes nem o próprio prompt, ignora instruções maliciosas).
4. **Modelo** e **Modelo em negociação**: vazio usa o de Configurações.
5. **Horário de atendimento humano**: fora dele a IA continua respondendo e avisa quando a equipe volta.
6. **Agente padrão**: marque em um só; ele atende quem não tem agente definido.
7. **Qualificação**: perguntas que a IA faz aos poucos (uma por mensagem), cada uma com peso. A soma vira a pontuação de 0 a 100 do contato.
8. **Intenções**: o que fazer quando o cliente demonstra cada intenção (continuar, transferir, priorizar, encerrar). Pedido de descadastro (SAIR) sempre encerra e bloqueia.
9. **Salvar**.

### Testar Agente
No mesmo menu, painel **Testar Agente**: escreva como se fosse o cliente e veja a resposta, a intenção detectada, a pontuação e as ações. Nada é gravado nem enviado. **Reiniciar** começa outra conversa.

### Laboratório IA
Menu **Laboratório IA**: 11 perfis de cliente prontos (iniciante, revendedor experiente, reclama de preço, quer painel Master, quer dividir com amigo, pede teste, pergunta de aplicativos, pede Pix, quer contratar, pede humano, pede para sair). Escolha um, edite as mensagens se quiser e clique em **Simular atendimento**. Marque **Gerar também a resposta do Claude** para ver a resposta real (gasta tokens). Use antes de ligar a IA com clientes reais e sempre que mudar a base ou o agente.

---

## 8. Mensagens (modelos de abordagem)

Menu **Mensagens**. Campanhas só enviam modelos **aprovados**.

1. **Escrever modelo**: nome, finalidade (Abordagem ou Acompanhamento) e texto. Variáveis: `{nome}`, `{, nome}` (vira ", Fulano" ou some se não houver nome) e `{assistente}`.
2. **Pré-visualizar** mostra como o contato recebe.
3. **Criar novo modelo** (ou **Nova versão do selecionado**).
4. Administrador: **Aprovar versão**. No modo Cloud API, informe o nome do template aprovado na Meta.

Toda mensagem identifica a assistente virtual e oferece a opção de sair (SAIR).

---

## 9. Contatos (leads)

Menu **Contatos**.

### Importar
- **Importar números**: arquivo CSV, TXT ou XLSX com uma coluna de números com DDD (com ou sem cabeçalho), ou cole os números um por linha. Não precisa de nome.
- **Nome da lista**: dê um nome à importação (ex.: `Leads Nov 25` ou `Grupos IPTV`). Ela vira uma pasta que você escolhe depois na campanha. Importar de novo com o mesmo nome acrescenta os números novos à mesma lista.
- **Números repetidos são ignorados automaticamente**: quem já existe no sistema (de qualquer lista) não entra de novo nem muda de lista. O resumo mostra quantos "já existiam".
- Informe **Origem** e **Tags** (ex.: `iptv, feira2026`).
- **Autorização da lista**: escreva como esses números autorizaram contato (ex.: "pediram informações no Instagram"). Se deixar em branco, os números ficam **pendentes** e **não recebem mensagens** até você registrar a autorização. Isso protege você na LGPD e nas regras do WhatsApp.

### Listas importadas
No topo da tela, o filtro **Lista** mostra só os contatos de uma importação. Com uma lista escolhida, dá para **Renomear lista** (ex.: chamar as importações antigas de `Primeiros 700`). Renomear para o nome de outra lista junta as duas.

### Autorizar depois
- Selecione vários na lista → escreva a origem da autorização → **Autorizar selecionados**.
- Ou **Autorizar em lote por tag**: autoriza todos com uma tag, inclusive os que não aparecem na tela. Descadastrados nunca são reautorizados.

### Outras ações
Cadastro manual, adicionar/remover tag em lote, excluir selecionados, registrar consentimento ou descadastro de um contato, exportar ou anonimizar dados de um titular (LGPD).

---

## 10. Agentes de disparo e campanhas

### Agente de disparo
Menu **Agentes de disparo** → **+ Novo agente**: nome, instância do WhatsApp (vazio = a de Integrações), horário e dias, **limite diário**, **intervalo mínimo** entre envios e o **agente de atendimento** que assume quando o contato responde.

### Campanha
Menu **Campanhas** → **+ Nova campanha**.
1. **Nome**, **Modelo de mensagem** (só aprovados) e **Agente de disparo**.
2. Filtro de público: **Listas de leads** (marque uma ou mais; nenhuma = todas), **Importados de / até** (período da importação, dd/mm/aaaa), **tags** e **segmento** (vazio = todos os autorizados).
   O sistema **nunca envia duas vezes para o mesmo número**: quem já recebeu mensagem, de qualquer campanha ou agente, fica de fora sozinho, e um envio repetido que chegue à fila é cancelado antes de sair.
3. **Data de início** (opcional), **horário** (início/fim) e **dias permitidos**.
4. **Máx. contatos**, **Limite/dia** e **Intervalo (min)** (mínimo de 15 minutos entre abordagens).
5. **Personalizar cada abordagem com IA** (opcional): a IA só varia a redação a partir do modelo aprovado, mantém empresa e opção SAIR e não inventa preço nem link. Fica desligado no modo Cloud API.
6. **Acompanhamento**: modelo aprovado de Acompanhamento e dias de cada etapa (ex.: `1,3,7`). Para sozinho quando o contato responde.
7. **Pausar se falhas > %** e **Pausar se descadastros > %**: a campanha se pausa sozinha se algo der errado.
8. **Salvar campanha** → **Validar** → **Pré-visualizar abordagens** → **▶ Iniciar envio**.

Controle: **❚❚ Pausar**, **▶ Retomar**, **Cancelar campanha**.

Quem recebe: só contatos **autorizados**, na etapa **Primeiro contato**, **nunca contatados antes** e fora da lista de bloqueio.

> Cuidado com o número: disparo em volume pelo Baileys (WhatsApp comum) pode levar a bloqueio. Comece com limite diário baixo (20 a 30) e intervalo alto.

---

## 11. Atendimento: Caixa de entrada e Kanban

### Caixa de entrada
Menu **Caixa de entrada**: todas as conversas, com busca por nome ou telefone.
- **ASSUMIR ATENDIMENTO**: pausa a IA nesta conversa e libera você para escrever.
- **Sugerir resposta (IA)**: a IA escreve um rascunho; você **Aceitar** (edita e envia) ou **Descartar**.
- **DEVOLVER PARA IA**: a IA volta a responder.
- Painel lateral: **Qualificação comercial** (experiência, painel, clientes ativos, dor, plano de interesse, prioridade, investimento, próxima ação), **Sugestão de plano**, **Responsável** (atribuir a um atendente) e **Memória do lead** (fatos que a IA lembra).

Quando a IA transfere, ela pausa sozinha, atribui ao atendente com menos conversas e deixa uma ficha com resumo, produtos, objeções e pendências.

### CRM Kanban
Menu **CRM Kanban**: um card por contato, nas etapas:

Primeiro contato → Abordagem realizada → Respondeu → Em atendimento → Qualificado → Em fase de teste → Proposta enviada → Em negociação → Negócio fechado (e também Em dúvidas e Não tem interesse).

- **Clique no card** para abrir a conversa do WhatsApp em uma janela, com os mesmos botões da Caixa de entrada.
- **Arraste** o card para mudar de etapa. "Negócio fechado" pede confirmação.
- A IA move os cards sozinha conforme a conversa avança (fica registrado).
- **Etapas** (administrador): renomear, reordenar e ocultar colunas. "Primeiro contato", "Negócio fechado" e "Não tem interesse" ficam sempre visíveis.
- Filtro por nome ou telefone.

---

## 12. Automações

Menu **Automações**: fluxos de blocos (se/então) que rodam a cada mensagem recebida, depois das regras fixas de descadastro e pedido de humano.

1. **+ Novo fluxo** → nome → **+ Bloco** para cada passo. Blocos disponíveis: Início, Evento recebido, Verificar consentimento, Verificar condição (etapa, tag, pontuação, palavra na mensagem), Analisar intenção, Consultar base, Executar IA, Enviar mensagem, Aguardar resposta, Atualizar CRM (mover etapa), Adicionar tag, Transferir para atendente e Encerrar.
2. Em cada condição ligue o caminho **sim** (↓) e **Se não →**.
3. **Validar e testar** com uma mensagem fictícia: mostra o caminho percorrido e as ações. Nada é salvo nem enviado.
4. Marque **Ativo** e **Salvar**.

Fluxos em círculo e ações conflitantes são recusados. "Aguardar resposta" encerra a execução; o fluxo recomeça do início na próxima mensagem.

---

## 13. Visão geral e relatórios

### Visão geral (Dashboard)
Números do período: contatos importados e elegíveis, campanhas ativas, mensagens enviadas, entregas, respostas, taxa de resposta, qualificados, transferências, propostas, vendas, descadastros, erros, tokens de IA, conversão por campanha, funil do Kanban e principais dores. Filtre por período, agente e campanha.

### Relatórios
- **Visão geral** do período.
- **Mensagens enviadas**: por campanha, por atendente/IA e por período, com Enviado, Entregue, Lido e Falhou (cada mensagem conta só no status atual), além dos motivos de falha.
- **Exportar leads (CSV)** e **Exportar mensagens (CSV)** para abrir no Excel.
- **Manutenção e LGPD** (administrador): **Fazer backup agora** (guarda os 20 mais recentes em `/var/lib/dominius-crm/backups`) e **Limpar registros técnicos** antigos (eventos de webhook e fila; não apaga conversas, contatos nem autorizações).

---

## 14. Manutenção da VPS

Entre por SSH no PowerShell do seu PC: `ssh root@164.68.121.142`

| Para | Comando |
|---|---|
| Ver se está rodando | `systemctl status dominius-crm` |
| Testar se responde | `curl -s http://127.0.0.1:5080/health` (deve mostrar `"status":"ok"` e a versão) |
| Logs ao vivo (Ctrl+C sai) | `journalctl -u dominius-crm -f` |
| Últimos avisos e erros | `journalctl -u dominius-crm -p warning -n 100` |
| Erros detalhados por dia | `ls /var/lib/dominius-crm/logs` e `tail -50 /var/lib/dominius-crm/logs/erros-AAAAMMDD.log` |
| Reiniciar | `systemctl restart dominius-crm` |
| Testar o nginx | `nginx -t` (só recarregue com `systemctl reload nginx` se der "ok") |
| Certificado HTTPS | renova sozinho; teste com `certbot renew --dry-run` |
| Espaço em disco | `df -h /` |

### Backup
- **Automático**: todo dia às 03:30 em `/var/lib/dominius-crm/backups/diario-AAAAMMDD.db`, guardando 14 dias.
- **Manual**: `sqlite3 /var/lib/dominius-crm/crm.db ".backup '/root/crm-$(date +%F).db'"` ou o botão em Relatórios.
- **Fora da VPS** (recomendado uma vez por semana), no PowerShell do seu PC:
  ```
  scp -r root@164.68.121.142:/var/lib/dominius-crm/backups C:\BackupCRM\
  scp -r root@164.68.121.142:/var/lib/dominius-crm/keys C:\BackupCRM\
  ```
  A pasta **keys** é o que permite ler as chaves da Anthropic e da Evolution salvas criptografadas. Sem ela, depois de restaurar você terá de colar as chaves de novo.

### Restaurar um backup
```
systemctl stop dominius-crm
rm -f /var/lib/dominius-crm/crm.db-wal /var/lib/dominius-crm/crm.db-shm
cp /var/lib/dominius-crm/backups/ARQUIVO.db /var/lib/dominius-crm/crm.db
chown dominius:dominius /var/lib/dominius-crm/crm.db
systemctl start dominius-crm
```

### Atualizar para versão nova
```
bash /opt/dominius-crm-src/deploy/linux/instalar.sh crm.minhafiberlink.com.br
```
O instalador faz backup do banco antes (`backups/antes-atualizar-*.db`), baixa a versão nova, compila, reinicia e refaz o HTTPS. Dados e configurações são mantidos. Leva de 3 a 10 minutos; durante esse tempo o CRM fica fora do ar.

Até as últimas mudanças entrarem no ramo principal do GitHub, use o ramo indicado na conversa:
```
BRANCH=claude/project-thread-8qdo7c bash /opt/dominius-crm-src/deploy/linux/instalar.sh crm.minhafiberlink.com.br
```

### Onde fica cada coisa
| Caminho | Conteúdo |
|---|---|
| `/var/lib/dominius-crm` | **Dados**: `crm.db`, `jwt.key`, `keys/`, `backups/`, `logs/` |
| `/opt/dominius-crm` | Programa compilado (trocado a cada atualização) |
| `/opt/dominius-crm-src` | Código-fonte usado para compilar |
| `/etc/systemd/system/dominius-crm.service` | Serviço |
| `/etc/nginx/sites-available/dominius-crm` | Proxy do domínio |
| `/etc/cron.d/dominius-crm-backup` | Backup diário |

A VPS também roda outros sites (nginx com vários domínios, MariaDB). O CRM usa só a porta interna 5080 e o seu próprio arquivo do nginx; não mexa nos arquivos dos outros sites.

---

## 15. Problemas comuns

| Sintoma | O que verificar |
|---|---|
| Site não abre | `systemctl status dominius-crm`; `nginx -t`; o DNS de `crm.minhafiberlink.com.br` aponta para 164.68.121.142? |
| Tela pede para criar administrador de novo | O banco foi trocado. Não crie outro: restaure o último backup. |
| IA não responde | IA ligada em Configurações? Chave da Anthropic salva? Limite diário de tokens estourou? A conversa foi assumida por humano (precisa **Devolver para IA**)? |
| Envia mas não recebe respostas | Clique em **Registrar webhook** em Integrações e confira o **Histórico de eventos recebidos**. A Evolution precisa alcançar `https://crm.minhafiberlink.com.br`. |
| Campanha não envia nada | Contatos autorizados? Na etapa Primeiro contato e nunca contatados? Modelo aprovado? Dentro do horário e dias? Campanha pausada por % de falhas? |
| Erro "Erro interno no servidor" na tela | O motivo fica em `/var/lib/dominius-crm/logs/erros-AAAAMMDD.log`. Mande esse trecho para análise. |
| Preço errado na resposta da IA | Corrija em **Planos** (nova versão + aprovar). A IA nunca usa preço do script nem da base. |
