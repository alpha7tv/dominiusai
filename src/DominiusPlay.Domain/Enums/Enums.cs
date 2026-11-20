namespace DominiusPlay.Domain.Enums;

/// <summary>As seis colunas principais do Kanban de vendas (nomes fixos).</summary>
public enum KanbanStage
{
    PrimeiroContato = 1,
    EmFaseDeTeste = 2,
    EmNegociacao = 3,
    NegocioFechado = 4,
    EmDuvidas = 5,
    NaoTemInteresse = 6,
}

/// <summary>Estado operacional do lead/conversa, separado da coluna do Kanban.</summary>
public enum ConversationMode
{
    AguardandoContato = 0,
    Ia = 1,
    AtendimentoHumano = 2,
    Pausado = 3,
    Encerrado = 4,
}

public enum ConsentStatus { Pendente = 0, Valido = 1, Revogado = 2 }

public enum MessageDirection { Entrada = 0, Saida = 1 }

/// <summary>Quem produziu a mensagem.</summary>
public enum MessageAuthor
{
    Contato = 0,
    Ia = 1,
    Atendente = 2,
    /// <summary>Mensagem enviada manualmente pelo celular/WhatsApp Web, detectada via webhook (fromMe=true sem registro no sistema).</summary>
    ManualNoAparelho = 3,
    Sistema = 4,
}

/// <summary>Status de entrega, alinhado com os status da Evolution API (ERROR, PENDING, SERVER_ACK, DELIVERY_ACK, READ, PLAYED).</summary>
public enum MessageStatus
{
    NaFila = 0,
    Enviando = 1,
    Pendente = 2,
    EnviadaServidor = 3,
    Entregue = 4,
    Lida = 5,
    Falhou = 6,
    Cancelada = 7,
    Recebida = 8,
}

public enum OutboundKind
{
    /// <summary>Primeira abordagem de campanha — sujeita ao intervalo mínimo por instância.</summary>
    AbordagemInicial = 0,
    /// <summary>Resposta a uma mensagem do contato — prioritária, sem intervalo de campanha.</summary>
    Resposta = 1,
    /// <summary>Acompanhamento (follow-up) conservador e configurável.</summary>
    Acompanhamento = 2,
    /// <summary>Mensagem digitada por atendente humano no CRM.</summary>
    Humana = 3,
    Teste = 4,
    /// <summary>Última mensagem da IA ao transferir para humano ou encerrar (avisa o cliente). Só dentro da janela de 24h.</summary>
    Transicao = 5,
}

public enum IptvExperience { NaoInformado = 0, JaTrabalha = 1, Iniciante = 2 }

public enum PainCategory
{
    Nenhuma = 0,
    TravamentosBuffering,
    QuedasServidor,
    SuporteInsuficiente,
    PrecosElevados,
    FaltaAutonomia,
    PainelLimitado,
    ProblemasAplicativos,
    DificuldadesClientesFinais,
    FaltaMarcaPropria,
    DificuldadesAtivacao,
    ProblemasFaturamento,
    NecessidadeExpansao,
    FaltaConfiancaFornecedor,
    Outros,
}

public enum Priority { Baixa = 0, Normal = 1, Alta = 2, Urgente = 3 }

public enum UserRole { Atendente = 0, Supervisor = 1, Administrador = 2 }

public enum ApprovalStatus { Rascunho = 0, EmRevisao = 1, Aprovado = 2, Arquivado = 3 }

public enum CampaignStatus { Rascunho = 0, Ativa = 1, Pausada = 2, Cancelada = 3, Concluida = 4 }

public enum FactSource
{
    /// <summary>Informação declarada pelo próprio cliente.</summary>
    DeclaradoPeloCliente = 0,
    /// <summary>Inferência da IA (precisa de confirmação).</summary>
    InferidoPelaIa = 1,
    /// <summary>Registrado por atendente humano.</summary>
    RegistradoPorAtendente = 2,
}

/// <summary>Tipo do agente de IA: atendimento (conversa com o cliente) ou disparo (prospecção em campanhas).</summary>
public enum AgentKind { Atendimento = 0, Disparo = 1 }

public enum AgentStatus { Ativo = 0, Pausado = 1, Inativo = 2 }

/// <summary>Intenção identificada na mensagem do contato. Cada intenção pode acionar um fluxo (ver IntentDetector / AgentProfile.IntentActions).</summary>
public enum IntentCategory
{
    Nenhuma = 0,
    PrimeiroContato = 1,
    InteresseComercial = 2,
    SolicitacaoPreco = 3,
    DuvidaTecnica = 4,
    Objecao = 5,
    Negociacao = 6,
    PedidoHumano = 7,
    SemInteresse = 8,
    Reclamacao = 9,
    Descadastro = 10,
    IntencaoCompra = 11,
}

/// <summary>Ação automática configurável para uma intenção.</summary>
public enum IntentAction
{
    /// <summary>A IA continua a conversa normalmente.</summary>
    Continuar = 0,
    /// <summary>Transfere para atendente humano (com ficha da conversa).</summary>
    TransferirHumano = 1,
    /// <summary>Marca prioridade alta e continua com a IA.</summary>
    PriorizarEContinuar = 2,
    /// <summary>Encerra a conversa (sem novos contatos automáticos).</summary>
    Encerrar = 3,
}
