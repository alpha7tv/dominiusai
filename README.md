# DOMINIUS PLAY AI CRM

Central de prospecção, atendimento, qualificação e vendas da Dominius Play para Windows.
Arquitetura completa em [docs/ARQUITETURA.md](docs/ARQUITETURA.md).

## Como executar (desenvolvimento)

Requisito: .NET SDK 10.

```bash
dotnet test                                   # 67 testes
dotnet run --project src/DominiusPlay.Server  # servidor em http://localhost:5080
dotnet run --project src/DominiusPlay.Desktop # aplicativo
```

No primeiro acesso o aplicativo pede para criar o usuário administrador.
Depois, em **Integrações**, informe a URL da Evolution API, a instância, a chave e a URL pública deste servidor,
clique em **Testar conexão** e **Registrar webhook**.

### Banco PostgreSQL (equipe com vários computadores)

`src/DominiusPlay.Server/appsettings.json`:

```json
"Database": { "Provider": "Postgres" },
"ConnectionStrings": { "Crm": "Host=...;Database=dominius_crm;Username=...;Password=..." }
```

## Gerar o instalador Windows

No Windows, com .NET 10 SDK e Inno Setup 6: `powershell -File installer\build.ps1`
→ `artifacts\installer\DominiusPlayCRM-Setup-0.2.0-x64.exe`.

Automático: o workflow `.github/workflows/build-windows.yml` roda em Windows real (runner do GitHub), executa todos os testes, gera o instalador, instala em modo silencioso com o Serviço do Windows, confere `/health` do servidor instalado e desinstala. O instalador fica como artefato da execução.

Dados ficam em `C:\ProgramData\DominiusPlay\CRM` (banco SQLite, chaves de criptografia) e são preservados em atualizações.

## Backup e manutenção

- **Backup (SQLite):** tela Relatórios → "Fazer backup agora" (ou `POST /api/maintenance/backup`). Cria `backups\crm-AAAAMMDD-HHMMSS.db` na pasta de dados e mantém os 20 mais recentes. Para restaurar: pare o serviço, copie o backup por cima de `crm.db` e inicie o serviço.
- **PostgreSQL:** use `pg_dump` no servidor do banco.
- **Limpeza:** remove eventos técnicos de webhook e itens antigos da fila (mínimo 7 dias). Leads, conversas e consentimentos não são apagados.
- **LGPD:** tela Leads → "Exportar dados do titular" (JSON completo) e "Excluir lead e todos os dados".

## Perfis

| Perfil | Pode |
|---|---|
| Atendente | Conversas, Kanban, leads, qualificação, assumir/devolver para a IA |
| Supervisor | + campanhas, modelos (criar/versão), relatórios, exportações, atribuir conversas, excluir lead |
| Administrador | + usuários, aprovar modelos e planos, integrações, IA, backup e limpeza |
