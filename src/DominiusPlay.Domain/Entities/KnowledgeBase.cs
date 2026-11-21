namespace DominiusPlay.Domain.Entities;

public enum KbStatus { Rascunho = 0, Publicado = 1, Inativo = 2 }

/// <summary>
/// Entrada da base de conhecimento comercial (v0.4.1), separada do prompt do agente: o administrador edita no painel
/// e a IA consulta a cada mensagem, sem reiniciar nada. Valores como chave Pix, links e preços ficam como marcadores
/// ({pix}, {link_teste}, {preco:MENSALISTA_10}…) e são preenchidos com a Configuração comercial e os Planos aprovados.
/// </summary>
public class KbEntry : Entity
{
    /// <summary>Nome da base (ex.: "Dominius Play"). Permite várias bases no futuro.</summary>
    public string Base { get; set; } = "Dominius Play";
    /// <summary>Identificador estável e legível (ex.: DP-001). Único por base.</summary>
    public string Code { get; set; } = "";
    public string Category { get; set; } = "";
    public string Question { get; set; } = "";
    /// <summary>Outras formas de perguntar a mesma coisa, uma por linha.</summary>
    public string Variations { get; set; } = "";
    public string Answer { get; set; } = "";
    /// <summary>Palavras-chave separadas por vírgula.</summary>
    public string Keywords { get; set; } = "";
    /// <summary>Códigos de planos/produtos relacionados, separados por vírgula (ex.: MENSALISTA_10,MENSALISTA_500).</summary>
    public string Products { get; set; } = "";
    /// <summary>1 (mais alta) a 5. Desempata respostas igualmente relevantes.</summary>
    public int Priority { get; set; } = 3;
    public KbStatus Status { get; set; } = KbStatus.Publicado;
    /// <summary>Fonte da informação (ex.: "Prompt Mestre Dominius Play — nov/2026").</summary>
    public string Source { get; set; } = "";
    /// <summary>A resposta depende de confirmação da equipe (a IA não afirma além do texto e oferece confirmar).</summary>
    public bool NeedsConfirmation { get; set; }
    public int Version { get; set; } = 1;
    public string UpdatedBy { get; set; } = "";
}

/// <summary>Histórico imutável de alterações de uma entrada (cópia completa de cada versão).</summary>
public class KbEntryRevision : Entity
{
    public Guid EntryId { get; set; }
    public string Code { get; set; } = "";
    public int Version { get; set; }
    /// <summary>criada, editada, publicada, desativada, importada, excluída.</summary>
    public string Action { get; set; } = "";
    public string ChangedBy { get; set; } = "";
    /// <summary>JSON com todos os campos da entrada nesta versão.</summary>
    public string Snapshot { get; set; } = "";
}
