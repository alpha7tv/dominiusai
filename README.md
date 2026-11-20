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
→ `artifacts\installer\DominiusPlayCRM-Setup-0.1.0-x64.exe`.

Dados ficam em `C:\ProgramData\DominiusPlay\CRM` (banco SQLite, chaves de criptografia) e são preservados em atualizações.
